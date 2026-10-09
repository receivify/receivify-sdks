/**
 * Offline test transport.
 *
 * Every test injects one of these as `{ fetch }`, so the suite never touches a network, never
 * needs a test framework, and can assert on the EXACT request the SDK built.
 */

/** Build a fetch-compatible stub from a handler, recording every call it receives. */
export function mockFetch(handler) {
  const calls = [];
  const fn = async (url, init = {}) => {
    const call = {
      url,
      method: init.method,
      headers: init.headers || {},
      body: init.body,
      json: init.body ? JSON.parse(init.body) : undefined,
      signal: init.signal,
    };
    calls.push(call);
    const spec = (await handler(call, calls.length)) || {};
    return makeResponse(spec);
  };
  fn.calls = calls;
  /** The single call, asserting there was exactly one. */
  fn.only = () => {
    if (calls.length !== 1) throw new Error(`expected exactly 1 request, got ${calls.length}`);
    return calls[0];
  };
  return fn;
}

/** A transport that answers every request with the same thing. */
export function constantFetch(spec) {
  return mockFetch(() => spec);
}

/** A transport that walks a list of canned responses, one per request. */
export function sequenceFetch(specs) {
  return mockFetch((_call, n) => {
    if (n > specs.length) throw new Error(`unexpected request #${n}`);
    return specs[n - 1];
  });
}

function makeResponse({ status = 200, body = null, headers = {}, text = undefined }) {
  const payload = text !== undefined ? text : body === null ? '' : JSON.stringify(body);
  const h = new Headers();
  for (const [k, v] of Object.entries(headers)) h.set(k, String(v));
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: h,
    async text() {
      return payload;
    },
  };
}

/** Parse the query string off a recorded request URL. */
export function queryOf(url) {
  const q = String(url).split('?')[1];
  return q ? Object.fromEntries(new URLSearchParams(q)) : {};
}

/** The path (no origin, no query) of a recorded request URL. */
export function pathOf(url) {
  return String(url).split('?')[0].replace(/^https?:\/\/[^/]+/, '');
}
