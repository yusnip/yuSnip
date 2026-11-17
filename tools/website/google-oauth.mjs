import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { pathToFileURL } from 'node:url';

const basePath = '/yusnip/oauth';
const redirectUri = 'http://127.0.0.1:38439/oauth/callback';
const googleTokenUrl = 'https://oauth2.googleapis.com/token';
const maxBodyBytes = 8192;

function reply(response, status, payload) {
    response.writeHead(status, {
        'Content-Type': 'application/json; charset=utf-8',
        'Cache-Control': 'no-store',
        'X-Content-Type-Options': 'nosniff'
    });
    response.end(JSON.stringify(payload));
}

async function readGrant(request) {
    const type = request.headers['content-type']?.split(';')[0].trim().toLowerCase();
    const length = Number(request.headers['content-length']);
    if (type !== 'application/x-www-form-urlencoded' || !Number.isInteger(length) ||
        length < 1 || length > maxBodyBytes)
        return null;

    const chunks = [];
    for await (const chunk of request)
        chunks.push(chunk);

    const form = new URLSearchParams(Buffer.concat(chunks).toString('utf8'));
    const grant = form.get('grant_type');
    if (grant === 'authorization_code' && form.size === 3 &&
        /^[!-~]{1,2048}$/.test(form.get('code') ?? '') &&
        /^[A-Za-z0-9._~-]{43,128}$/.test(form.get('code_verifier') ?? '')) {
        return new URLSearchParams({
            grant_type: grant,
            code: form.get('code'),
            code_verifier: form.get('code_verifier'),
            redirect_uri: redirectUri
        });
    }

    if (grant === 'refresh_token' && form.size === 2 &&
        /^[!-~]{1,4096}$/.test(form.get('refresh_token') ?? '')) {
        return new URLSearchParams({ grant_type: grant, refresh_token: form.get('refresh_token') });
    }

    return null;
}

export function createOAuthServer({ client_id, client_secret }, exchange = fetch) {
    if (typeof client_id !== 'string' || !/^[A-Za-z0-9._-]+\.apps\.googleusercontent\.com$/.test(client_id) ||
        typeof client_secret !== 'string' || !/^[A-Za-z0-9_-]+$/.test(client_secret)) {
        throw new Error('Invalid desktop OAuth configuration');
    }

    return createServer({ requestTimeout: 10000, headersTimeout: 5000 }, async (request, response) => {
        if (request.method === 'GET' && request.url === basePath + '/client') {
            reply(response, 200, { client_id });
            return;
        }

        if (request.method !== 'POST' || request.url !== basePath + '/token') {
            reply(response, 404, { error: 'not_found' });
            return;
        }

        try {
            const form = await readGrant(request);
            if (!form) {
                reply(response, 400, { error: 'invalid_request' });
                return;
            }

            form.set('client_id', client_id);
            form.set('client_secret', client_secret);
            const upstream = await exchange(googleTokenUrl, {
                method: 'POST',
                body: form,
                redirect: 'error',
                signal: AbortSignal.timeout(8000)
            });
            const token = await upstream.json();
            if (upstream.ok && typeof token.access_token === 'string') {
                reply(response, 200, {
                    access_token: token.access_token,
                    refresh_token: token.refresh_token,
                    expires_in: token.expires_in,
                    scope: token.scope,
                    token_type: token.token_type
                });
                return;
            }

            if (token.error === 'invalid_grant') {
                reply(response, 400, { error: 'invalid_grant' });
                return;
            }

            reply(response, 502, { error: 'oauth_unavailable' });
        } catch {
            if (!response.destroyed)
                reply(response, 502, { error: 'oauth_unavailable' });
        }
    });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const path = process.env.CREDENTIALS_DIRECTORY
        ? process.env.CREDENTIALS_DIRECTORY + '/desktop-oauth.json'
        : process.argv[2];
    const { installed } = JSON.parse(readFileSync(path, 'utf8'));
    createOAuthServer(installed).listen(18765, '127.0.0.1');
}
