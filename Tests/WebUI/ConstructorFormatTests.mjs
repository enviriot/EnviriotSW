import assert from 'node:assert/strict';
import { test } from 'node:test';
import { formatConstructorValue, parseConstructorValue } from '../../Output/www/ide/services/value-format.js';

for(const [format, value, expected] of [['0x{0:X2}', 118, '0x76'], ['{0:F2}', 12.5, '12.50'], ['{0,6:D3}', 7, '   007']]) {
  test(`constructor preview ${format} agrees with invariant server fixture`, () => {
    assert.deepEqual(formatConstructorValue(value, format), { text: expected, error: null });
  });
}
test('aliases and unset values are not numerically formatted', () => {
  assert.equal(formatConstructorValue('SDA_ALIAS', '0x{0:X2}').text, 'SDA_ALIAS');
  assert.equal(formatConstructorValue(null, '0x{0:X2}').text, '<unset>');
  assert.equal(parseConstructorValue('118'), 118);
  assert.equal(parseConstructorValue('false'), false);
  assert.equal(parseConstructorValue('SDA_ALIAS'), 'SDA_ALIAS');
  for(const bad of ['NaN', 'Infinity', 'null', '1 + 2', 'Block.Q', '1e999']) assert.throws(() => parseConstructorValue(bad));
});
test('oversized formats fall back without changing the input', () => {
  const value = 118;
  assert.ok(formatConstructorValue(value, '{0,100000000}').error);
  assert.equal(formatConstructorValue(value, '{0,100000000}').text, '118');
  for(const format of ['{0:hex2}', '{0:Q2}', '{1}', '{0,999}', '{0:F99}', '{0']) {
    assert.ok(formatConstructorValue(value, format).error);
    assert.equal(formatConstructorValue(value, format).text, '118');
  }
});
