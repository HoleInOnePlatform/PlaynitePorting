const expected = '{"type":"rest_site_reached"}';
export function isRestSiteSignal(message) {
  if (!(message instanceof Uint8Array) || message.byteLength === 0 || message.byteLength > 256) return false;
  try {
    const value = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(message));
    return value !== null && typeof value === 'object' && !Array.isArray(value) &&
      Object.keys(value).length === 1 && value.type === 'rest_site_reached';
  } catch { return false; }
}

export function forwardRestSiteSignal(message, bridge = globalThis.CefSharp) {
  if (!isRestSiteSignal(message) || globalThis.top !== globalThis.self) return false;
  if (typeof bridge?.PostMessage !== 'function') return false;
  bridge.PostMessage(expected);
  return true;
}
