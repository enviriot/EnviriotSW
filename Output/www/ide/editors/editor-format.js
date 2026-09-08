export function formatDefault(value) {
  if(value === undefined || value === null) return '';
  if(typeof value === 'string') return value;
  if(typeof value === 'number' || typeof value === 'boolean') return String(value);
  try { return JSON.stringify(value); }
  catch { return String(value); }
}

// Grows a textarea to fit its content - the caller's CSS max-height (70vh for the
// String/JS "value" view editors) clips the actual rendered height beyond that and
// switches the textarea's own overflow to scroll, so this never needs to know the cap.
export function autoGrowTextarea(textarea) {
  if(!textarea) return;
  textarea.style.height = 'auto';
  textarea.style.height = `${textarea.scrollHeight}px`;
}
