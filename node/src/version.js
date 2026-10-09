// Kept in its own module so `package.json` never has to be imported at runtime
// (importing JSON needs an import attribute and would break the CJS interop story).
export const VERSION = '1.0.0';
export const USER_AGENT = `receivify-node/${VERSION}`;
