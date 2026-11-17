import json
import os
import re
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import parse_qsl, urlencode
from urllib.request import HTTPRedirectHandler, Request, build_opener


BASE_PATH = "/yusnip/oauth"
GOOGLE_TOKEN_URL = "https://oauth2.googleapis.com/token"
REDIRECT_URI = "http://127.0.0.1:38439/oauth/callback"


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def read_grant(headers, stream):
    if headers.get_content_type() != "application/x-www-form-urlencoded":
        return None
    length = int(headers.get("Content-Length", "0"))
    if not 1 <= length <= 8192:
        return None

    pairs = parse_qsl(stream.read(length).decode("utf-8"), strict_parsing=True, max_num_fields=3)
    form = dict(pairs)
    grant = form.get("grant_type")
    if len(form) != len(pairs):
        return None
    if (grant == "authorization_code" and form.keys() == {"grant_type", "code", "code_verifier"}
            and re.fullmatch(r"[!-~]{1,2048}", form["code"])
            and re.fullmatch(r"[A-Za-z0-9._~-]{43,128}", form["code_verifier"])):
        return {**form, "redirect_uri": REDIRECT_URI}
    if (grant == "refresh_token" and form.keys() == {"grant_type", "refresh_token"}
            and re.fullmatch(r"[!-~]{1,4096}", form["refresh_token"])):
        return form
    return None


def create_handler(client, opener):
    client_id = client["client_id"]
    client_secret = client["client_secret"]
    if (not re.fullmatch(r"[A-Za-z0-9._-]+\.apps\.googleusercontent\.com", client_id)
            or not re.fullmatch(r"[A-Za-z0-9_-]+", client_secret)):
        raise ValueError("Invalid desktop OAuth configuration")

    class Handler(BaseHTTPRequestHandler):
        timeout = 10

        def log_message(self, *_):
            pass

        def reply(self, status, payload):
            body = json.dumps(payload).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            if self.path == BASE_PATH + "/client":
                self.reply(200, {"client_id": client_id})
            else:
                self.reply(404, {"error": "not_found"})

        def do_POST(self):
            if self.path != BASE_PATH + "/token":
                self.reply(404, {"error": "not_found"})
                return

            try:
                form = read_grant(self.headers, self.rfile)
            except (ValueError, OSError):
                form = None
            if form is None:
                self.reply(400, {"error": "invalid_request"})
                return

            form.update(client_id=client_id, client_secret=client_secret)
            request = Request(GOOGLE_TOKEN_URL, data=urlencode(form).encode(), method="POST")
            try:
                try:
                    with opener.open(request, timeout=8) as upstream:
                        token = json.loads(upstream.read(65536))
                    if isinstance(token.get("access_token"), str):
                        fields = ("access_token", "refresh_token", "expires_in", "scope", "token_type")
                        self.reply(200, {key: token[key] for key in fields if key in token})
                        return
                except HTTPError as error:
                    with error:
                        if json.loads(error.read(65536)).get("error") == "invalid_grant":
                            self.reply(400, {"error": "invalid_grant"})
                            return
            except (ValueError, OSError, AttributeError):
                pass
            self.reply(502, {"error": "oauth_unavailable"})

    return Handler


if __name__ == "__main__":
    directory = os.environ.get("CREDENTIALS_DIRECTORY")
    path = Path(directory, "desktop-oauth.json") if directory else Path(sys.argv[1])
    client = json.loads(path.read_text(encoding="utf-8"))["installed"]
    handler = create_handler(client, build_opener(NoRedirect()))
    with ThreadingHTTPServer(("127.0.0.1", 18765), handler) as server:
        server.serve_forever()
