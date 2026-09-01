export function getView(vid) {
  return partsOf(vid)[0] || '';
}

export function getTopicPath(vid) {
  return partsOf(vid)[1] || '';
}

export function getFieldPath(vid) {
  const parts = partsOf(vid);
  return parts.length > 2 ? parts.slice(2).join('#') : '';
}

export function getName(vid) {
  const fieldPath = getFieldPath(vid);
  if(fieldPath) return lastSegment(fieldPath, '.');
  const topicPath = getTopicPath(vid);
  if(!topicPath || topicPath === '/') return '';
  return lastSegment(topicPath, '/');
}

export function getParentVid(vid, name = getName(vid)) {
  const view = getView(vid);
  const topicPath = getTopicPath(vid);
  const fieldPath = getFieldPath(vid);
  if(!view) return '';
  if(fieldPath) {
    const parentField = removeTrailingName(fieldPath, name, '.');
    return parentField ? `${view}#${topicPath}#${parentField}` : `${view}#${topicPath}`;
  }
  const parentTopic = parentTopicPath(topicPath, name);
  return `${view}#${parentTopic}`;
}

export function isDescendant(parentVid, candidateVid) {
  if(!parentVid || !candidateVid || parentVid === candidateVid) return false;
  const parentLevel = vidLevel(parentVid);
  const candidateLevel = vidLevel(candidateVid);
  if(candidateLevel <= parentLevel) return false;
  return getParentChain(candidateVid).includes(parentVid);
}

export function compareVid(a, b) {
  return String(a || '').localeCompare(String(b || ''), undefined, { sensitivity: 'variant' });
}

export function vidLevel(vid) {
  const topicPath = getTopicPath(vid);
  const fieldPath = getFieldPath(vid);
  const topicLevel = topicPath === '/' || topicPath === '' ? 0 : topicPath.split('/').filter(Boolean).length;
  const fieldLevel = fieldPath ? fieldPath.split('.').filter(Boolean).length : 0;
  return topicLevel + fieldLevel;
}

function getParentChain(vid) {
  const chain = [];
  let current = vid;
  for(let guard = 0; guard < 256; guard++) {
    const parent = getParentVid(current);
    if(!parent || parent === current) break;
    chain.push(parent);
    current = parent;
  }
  return chain;
}

function partsOf(vid) {
  return String(vid || '').split('#');
}

function lastSegment(value, separator) {
  const idx = value.lastIndexOf(separator);
  return idx < 0 ? value : value.slice(idx + 1);
}

function removeTrailingName(value, name, separator) {
  if(!value) return '';
  const suffix = `${separator}${name}`;
  if(name && value.endsWith(suffix)) return value.slice(0, -suffix.length);
  if(name && value === name) return '';
  const idx = value.lastIndexOf(separator);
  return idx < 0 ? '' : value.slice(0, idx);
}

function parentTopicPath(topicPath, name) {
  if(!topicPath || topicPath === '/') return '/';
  const suffix = `/${name}`;
  if(name && topicPath.endsWith(suffix)) {
    const parent = topicPath.slice(0, -suffix.length);
    return parent || '/';
  }
  const idx = topicPath.lastIndexOf('/');
  return idx <= 0 ? '/' : topicPath.slice(0, idx);
}
