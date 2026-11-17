# Google Drive 授权服务

官网版和 GitHub 版共用 `https://yusnip.top/yusnip/oauth`。客户端用 `GET /client` 读取桌面 OAuth 客户端 ID，通过系统浏览器在 Google 完成 PKCE 授权，再用 `POST /token` 交换授权码或刷新令牌。同步文件由客户端直接和 Google Drive 传输。

使用仍可访问旧备份的桌面 OAuth 客户端。将 Google 的 `installed` 凭据 JSON 保存到服务器的非公开目录并限制文件访问。运行 `python3 google-oauth.py /path/to/desktop-oauth.json`，或通过 systemd 的 `LoadCredential=desktop-oauth.json:/path/to/desktop-oauth.json` 提供配置。进程只监听 `127.0.0.1:18765`。当服务器需经已有的 HTTPS 代理访问 Google 时，为服务设置 `https_proxy`。

在 Nginx 的 `http` 中配置每 IP 限流区。

```nginx
limit_req_zone $binary_remote_addr zone=yusnip_google_oauth:10m rate=2r/s;
```

在 `yusnip.top` 的 HTTPS 站点中配置转发。授权请求和令牌只经 TLS 传输，接口关闭访问日志与代理缓存。

```nginx
location ^~ /yusnip/oauth/ {
    limit_req zone=yusnip_google_oauth burst=10 nodelay;
    limit_req_status 429;
    client_max_body_size 8k;
    access_log off;
    proxy_cache off;
    proxy_pass http://127.0.0.1:18765;
}
```

公开发版前验证 `/yusnip/oauth/client` 只返回客户端 ID，向 `/yusnip/oauth/token` 提交无效授权码时返回 `invalid_grant`，然后用空白配置从客户端完成浏览器授权和 Drive 权限校验。
