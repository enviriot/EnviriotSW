// Grid arithmetic for x13-graph: which step to ask for, where the window snaps, what is already
// held and what still has to be fetched. Kept apart from graph.js because none of it touches the
// DOM or the network, which is what lets the server-side test suite run it under the project's own
// JS engine - the browser is otherwise the only place this could be checked at all.

// Steps the chart may ask for, in seconds.
//
// The list is not arbitrary. The server rounds the step up to a whole number of stored buckets -
// five minutes, an hour, a day, whichever rung of its ladder the step lands on - and then snaps the
// window to multiples of that step counted from the zero of the calendar. Every value here of five
// minutes or more is already a whole number of five-minute buckets, every value of an hour or more
// a whole number of hours, and every value of a day or more a whole number of days. So the server's
// rounding is a no-op, and the client knows exactly which grid the answer will arrive on. Get this
// wrong and the two disagree about where points sit, which is precisely the bug the grid was
// introduced to remove.
export const GRID_STEPS = [
  1, 2, 5, 10, 15, 20, 30,                      // seconds
  60, 120, 300, 600, 900, 1800,                 // minutes
  3600, 7200, 10800, 21600, 43200,              // hours
  86400, 172800, 604800                         // days
];

/// The coarsest step no wider than the caller wants, in milliseconds.
/// Rounded UP, so the answer never has more points than were asked for.
export function pickStep(spanMs, points) {
  let want = spanMs / Math.max(1, points) / 1000;
  for (let i = 0; i < GRID_STEPS.length; i++) {
    if (GRID_STEPS[i] >= want) {
      return GRID_STEPS[i] * 1000;
    }
  }
  return GRID_STEPS[GRID_STEPS.length - 1] * 1000;
}

/// The window widened outward to the grid the given step defines.
export function snapWindow(fromMs, toMs, stepMs) {
  return [Math.floor(fromMs / stepMs) * stepMs, Math.ceil(toMs / stepMs) * stepMs];
}

/// What has to be fetched so that [b, e] is covered, given what is already held.
///
/// Returns { mode, from, to }. "none" means the cache already covers the view and nothing at all
/// needs to be asked for - that is the case a pan should normally fall into, and the reason for
/// fetching a margin around the view rather than only the view. "merge" extends the cache at one
/// end; "replace" throws it away, which happens when the step changed (a zoom) or when the view
/// jumped clear of what is held.
export function planFetch(cache, b, e, bx, ex, stepMs) {
  if (!cache || cache.step !== stepMs || !cache.rows || cache.rows.length === 0) {
    return { mode: 'replace', from: bx, to: ex };
  }
  if (b >= cache.from && e <= cache.to) {
    return { mode: 'none', from: 0, to: 0 };
  }
  if (b > cache.to || e < cache.from) {
    // Nothing in common - a jump to another part of the history rather than a pan. Extending the
    // cache to reach it would fetch the whole gap in between and keep rows nobody is looking at;
    // this case was found by a test that expected a replace and got a merge spanning it.
    return { mode: 'replace', from: bx, to: ex };
  }
  if (b >= cache.from && e > cache.to) {
    return { mode: 'merge', from: cache.to, to: ex };      // panned or grew to the right
  }
  if (e <= cache.to && b < cache.from) {
    return { mode: 'merge', from: bx, to: cache.from };    // ...to the left
  }
  return { mode: 'replace', from: bx, to: ex };            // wider on both sides, or jumped away
}

/// Two grid-aligned row sets into one, sorted, trimmed to [lo, hi].
///
/// Rows are [instant, v1, v2, ...] and the instant may be a Date or a number; unary plus takes
/// either. On a tie the NEW row wins, which is not a detail: the last bucket of any answer is
/// partial - it stops at "now" rather than at the end of its interval - so when the window later
/// extends past it, the same bucket comes back complete and has to overwrite the stub.
export function mergeRows(oldRows, newRows, lo, hi) {
  let out = [];
  let i = 0, j = 0;
  let a = oldRows || [], bb = newRows || [];
  while (i < a.length || j < bb.length) {
    let ta = i < a.length ? +a[i][0] : Infinity;
    let tb = j < bb.length ? +bb[j][0] : Infinity;
    let row;
    if (ta < tb) {
      row = a[i++];
    } else if (tb < ta) {
      row = bb[j++];
    } else {
      row = bb[j++];
      i++;
    }
    let t = +row[0];
    if (t >= lo && t <= hi) {
      out.push(row);
    }
  }
  return out;
}

// How far the window has to move before it is worth asking the server again, as a fraction of the
// window itself. It used to be a flat 60 s, which on a two-day window is 0.035% - about two thirds
// of one pixel on a 1900 px chart, and on a year view four thousandths of a pixel. In other words
// every mouse movement counted as a new window. One percent is about 19 px of panning, which at
// 500 points is five points of change: below that there is nothing new to see.
const MOVED_FRACTION = 0.01;
// ...but not less than this, so a window of a few minutes still refreshes.
const MOVED_FLOOR_MS = 1000;

/// Did the window move enough to be worth a request?
export function windowMoved(oldRange, newRange) {
  let span = Math.abs(newRange[1] - newRange[0]);
  let eps = Math.max(span * MOVED_FRACTION, MOVED_FLOOR_MS);
  return Math.abs(oldRange[0] - newRange[0]) > eps || Math.abs(oldRange[1] - newRange[1]) > eps;
}
