const assetPaths = new Set([
  "/",
  "/index.html",
  "/privacy",
  "/privacy.html",
  "/styles.css",
]);

function isLandingAsset(pathname) {
  return assetPaths.has(pathname) || pathname.startsWith("/assets/");
}

function withSecurityHeaders(response) {
  const headers = new Headers(response.headers);
  headers.set("x-content-type-options", "nosniff");
  headers.set("referrer-policy", "strict-origin-when-cross-origin");
  headers.set("permissions-policy", "camera=(), microphone=(), geolocation=()");
  headers.set("content-security-policy", "default-src 'self'; img-src 'self' data:; style-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'");
  headers.set("cache-control", "public, max-age=300");
  return new Response(response.body, { status: response.status, headers });
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (isLandingAsset(url.pathname)) {
      if (url.pathname === "/") {
        url.pathname = "/index.html";
      } else if (url.pathname === "/privacy") {
        url.pathname = "/privacy.html";
      }

      const assetRequest = new Request(url, request);
      return withSecurityHeaders(await env.ASSETS.fetch(assetRequest));
    }

    // Velopack manifest、nupkg、Setup.exe は R2 Custom Domain へそのまま委譲する。
    // Range・ETag・Content-Type・CDN キャッシュを Worker で変更しない。
    return fetch(request);
  },
};

