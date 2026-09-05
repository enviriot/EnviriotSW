// Per-session token handed to us in resp.hello. The HTTP endpoints (/api/export,
// /api/import) reject anything without it, which keeps them from being usable by a
// drive-by request - notably a no-cors POST from any other site the user has open.
// It lives only as long as the WebSocket session that issued it.
let token = '';

export function setApiToken(value) {
  token = value || '';
}

export function apiUrl(path) {
  if(!token) return path;
  const separator = path.includes('?') ? '&' : '?';
  return `${path}${separator}t=${encodeURIComponent(token)}`;
}
