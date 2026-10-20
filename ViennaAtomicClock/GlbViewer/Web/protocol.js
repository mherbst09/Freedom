// Validation of commands coming from Freedom (WPF). Anything that does not match is rejected.

const BACKGROUNDS = new Set(['Dark', 'Light', 'Transparent', 'Studio']);
const VIEW_MODES = new Set(['normal', 'unlit', 'wireframe', 'basecolor', 'normals', 'metalrough']);
const SPEEDS = new Set([0.25, 0.5, 1, 1.5, 2]);
const ANIMATION_ACTIONS = new Set(['play', 'pause', 'toggle', 'stop', 'seek', 'clip', 'loop', 'speed']);
export const MODEL_ORIGIN = 'https://model.freedom-viewer.local/';

const isNumber = (v, min = -Infinity, max = Infinity) => typeof v === 'number' && Number.isFinite(v) && v >= min && v <= max;
const isBool = (v) => typeof v === 'boolean';
const isId = (v) => Number.isInteger(v) && v >= 0 && v < 10_000_000;
const isVector = (v, check) => Array.isArray(v) && v.length === 3 && v.every(check);

const rules = {
  load: (m) => typeof m.url === 'string' && m.url.startsWith(MODEL_ORIGIN) && m.url.length < 4096
    && typeof m.name === 'string' && m.name.length < 1024 && isNumber(m.size, 0),
  cancelLoad: () => true,
  unload: () => true,
  resetView: () => true,
  centerModel: () => true,
  showAll: () => true,
  requestStats: () => true,
  settings: (m) => BACKGROUNDS.has(m.background) && isNumber(m.exposure, 0.1, 4) && isNumber(m.light, 0, 4)
    && isBool(m.shadows) && isBool(m.grid) && isBool(m.axes) && SPEEDS.has(m.speed) && isBool(m.autoRotate),
  focus: (m) => m.id === null || isId(m.id),
  select: (m) => m.id === null || isId(m.id),
  setVisible: (m) => isId(m.id) && isBool(m.visible),
  setTransform: (m) => isId(m.id) && isVector(m.position, (x) => isNumber(x, -1e7, 1e7))
    && isVector(m.rotation, (x) => isNumber(x, -36000, 36000)) && isVector(m.scale, (x) => isNumber(x, -1e6, 1e6) && Math.abs(x) > 1e-9),
  viewMode: (m) => VIEW_MODES.has(m.mode),
  animation: (m) => ANIMATION_ACTIONS.has(m.action)
    && (m.action !== 'seek' || isNumber(m.value, 0))
    && (m.action !== 'clip' || isId(m.value))
    && (m.action !== 'loop' || isBool(m.value))
    && (m.action !== 'speed' || SPEEDS.has(m.value)),
  screenshot: (m) => (m.target === 'file' || m.target === 'clipboard') && isBool(m.transparent),
  useCamera: (m) => isId(m.id),
  language: (m) => m.texts !== null && typeof m.texts === 'object' && Object.values(m.texts).every((t) => typeof t === 'string' && t.length < 2000)
};

/** Returns the command if it is valid, otherwise null. */
export function validateCommand(message) {
  if (message === null || typeof message !== 'object' || Array.isArray(message)) return null;
  const rule = Object.prototype.hasOwnProperty.call(rules, message.type) ? rules[message.type] : null;
  if (!rule) return null;
  try {
    return rule(message) ? message : null;
  } catch {
    return null;
  }
}
