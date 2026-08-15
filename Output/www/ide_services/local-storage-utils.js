export function readPositiveNumber(key, fallback) {
  const value = Number(localStorage.getItem(key));
  return Number.isFinite(value) && value > 0 ? value : fallback;
}

export function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

export function readBoolean(key, fallback) {
  const value = localStorage.getItem(key);
  return value === null ? fallback : value === 'true';
}
