import '../../lib/stringformat.min.js';

export function formatConstructorValue(value, format) {
  if(value === null || value === undefined) return { text: '<unset>', error: null };
  const fallback = String(value);
  if(fallback.length > 256) return { text: fallback.slice(0, 255) + '…', error: 'Display value is too long' };
  if(format === null || format === undefined) return { text: fallback, error: null };
  try {
    if(typeof format !== 'string' || format.length > 256) throw new Error('Format is too long');
    const token = /(?:[^{}]+|\{\{|\}\}|\{0(?:,\s*(-?\d{1,3}))?(?::([dDxXfFeEgG])(\d{1,2})?)?\})/y;
    let offset = 0;
    while(offset < format.length) {
      token.lastIndex = offset;
      const match = token.exec(format);
      if(!match || Math.abs(Number(match[1]) || 0) > 256 || Number(match[3]) > 16) throw new Error('Unsupported composite format');
      offset = token.lastIndex;
    }
    if(typeof value === 'string') return { text: fallback, error: null };
    const text = String.format(format, value);
    if(text.length > 256) throw new Error('Formatted value is too long');
    return { text, error: null };
  } catch(error) {
    return { text: fallback, error: String(error?.message || error) };
  }
}

export function parseConstructorValue(text) {
  const value = String(text).trim();
  if(value === 'true') return true;
  if(value === 'false') return false;
  if(/^[A-Za-z_][A-Za-z0-9_]*$/.test(value) && !/^(?:NaN|Infinity|undefined|null)$/.test(value)) return value;
  if(!/^-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?$/.test(value)) throw new Error('Expected a finite number, Boolean, or device constant');
  const number = Number(value);
  if(!Number.isFinite(number)) throw new Error('Expected a finite number');
  return number;
}
