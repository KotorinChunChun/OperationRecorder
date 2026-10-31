// 専用生成HTMLをヘッドレスブラウザーで検証する。通常ブラウザーのプロファイルは使わない。
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const assert = require('node:assert/strict');

const fixtures = process.argv[2] && path.resolve(process.argv[2]);
const output = process.argv[3] && path.resolve(process.argv[3]);
if (!fixtures || !output) {
  console.error('使い方: node dev/scripts/Test-ReportHtml.cjs <生成HTMLフォルダー> <結果フォルダー>');
  process.exit(2);
}
const history = path.resolve(__dirname, '../history');
for (const directory of [fixtures, output]) {
  const relative = path.relative(history, directory);
  if (relative.startsWith('..') || path.isAbsolute(relative) || !relative.split(path.sep).includes('logs')) {
    throw new Error('入力・結果は対象版のdev/history/vX.Y.Z/logs配下へ指定してください。');
  }
}
fs.mkdirSync(output, { recursive: true });
const results = [];
let browser;

async function allVisible(locator, visible) {
  assert.ok(await locator.count() > 0);
  for (const item of await locator.all()) assert.equal(await item.isVisible(), visible);
}
async function verify(name, action) {
  await action();
  results.push({ name, status: 'passed' });
  console.log('PASS: ' + name);
}

(async () => {
  const { chromium } = require(process.env.OPREC_PLAYWRIGHT_MODULE || 'playwright');
  browser = await chromium.launch({ channel: process.env.OPREC_HTML_BROWSER || 'msedge', headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  // 生成されたローカルHTMLと埋込画像だけを読む。
  await context.route('**/*', route => /^https?:/i.test(route.request().url()) ? route.abort() : route.continue());
  const page = await context.newPage();
  for (const mode of ['before', 'after', 'both']) {
    await page.goto(pathToFileURL(path.join(fixtures, mode + '.html')).href);
    await page.locator('img').first().waitFor();
    assert.equal(await page.locator('img').count(), mode === 'both' ? 8 : 4);
    await page.waitForFunction(() => Array.from(document.images).every(image => image.complete && image.naturalWidth > 0));
    await verify(mode + ': 全手順の画像と切替の有無', async () => {
      assert.equal(await page.locator('.view-switch').count(), mode === 'both' ? 1 : 0);
      if (mode !== 'both') {
        const side = mode === 'before' ? '前' : '後';
        for (let number = 1; number <= 4; number++) assert.equal(await page.locator('img[alt="手順 ' + number + ' 操作' + side + '"]').count(), 1);
      }
    });
    if (mode === 'both') {
      await verify('both: 操作前/後の切替が全4手順へ作用する', async () => {
        await page.getByText('操作前だけ', { exact: true }).click();
        await allVisible(page.locator('.shot-before img'), true); await allVisible(page.locator('.shot-after img'), false);
        await page.getByText('操作後だけ', { exact: true }).click();
        await allVisible(page.locator('.shot-before img'), false); await allVisible(page.locator('.shot-after img'), true);
        await page.getByText('操作前と操作後', { exact: true }).click();
        await allVisible(page.locator('.shot-before img'), true); await allVisible(page.locator('.shot-after img'), true);
      });
    }
    await verify(mode + ': 右上の閉じるボタンとEscで解除する', async () => {
      await page.locator('.zoom-open').first().click();
      assert.equal(await page.locator('.zoom:target').count(), 1);
      const close = page.getByRole('button', { name: '閉じる（ESC）', exact: true });
      const box = await close.boundingBox();
      assert.ok(box && box.y >= 0 && box.y < 40 && box.x + box.width > 1240 && box.x + box.width <= 1280);
      await page.screenshot({ path: path.join(output, mode + '-zoom.png') });
      await close.click(); await page.waitForFunction(() => !document.querySelector('.zoom:target'));
      await page.locator('.zoom-open').last().click();
      assert.equal(await page.locator('.zoom:target').count(), 1);
      await page.keyboard.press('Escape');
      await page.waitForFunction(() => !document.querySelector('.zoom:target'));
      assert.equal(await page.locator('.zoom:target').count(), 0);
      assert.equal(await page.evaluate(() => location.hash), '#_');
      await page.keyboard.press('Escape'); assert.equal(await page.locator('.zoom:target').count(), 0);
    });
  }
  await verify('狭い画面でも閉じるボタンが右上に収まる', async () => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.locator('.zoom-open').last().click();
    const box = await page.getByRole('button', { name: '閉じる（ESC）', exact: true }).boundingBox();
    assert.ok(box && box.x >= 0 && box.y < 40 && box.x + box.width <= 390);
    await page.screenshot({ path: path.join(output, 'narrow-zoom.png') });
    await page.keyboard.press('Escape'); assert.equal(await page.locator('.zoom:target').count(), 0);
  });
  await context.close();
  const noScript = await browser.newContext({ javaScriptEnabled: false });
  const fallback = await noScript.newPage();
  await verify('JavaScript無効でもクリックで拡大と閉じるが使える', async () => {
    await fallback.goto(pathToFileURL(path.join(fixtures, 'both.html')).href);
    await fallback.locator('.zoom-open').first().click();
    assert.equal(await fallback.locator('.zoom:target').count(), 1);
    await fallback.getByRole('button', { name: '閉じる（ESC）', exact: true }).click();
    assert.equal(await fallback.locator('.zoom:target').count(), 0);
  });
  await noScript.close();
})().then(() => {
  fs.writeFileSync(path.join(output, 'summary.json'), JSON.stringify({ schemaVersion: 1, status: 'passed', passed: results.length, failed: 0, cases: results }, null, 2));
}).catch(error => {
  console.error(error.stack);
  fs.writeFileSync(path.join(output, 'summary.json'), JSON.stringify({ schemaVersion: 1, status: 'failed', passed: results.length, failed: 1, cases: results, error: error.stack }, null, 2));
  process.exitCode = 1;
}).finally(async () => { if (browser) await browser.close(); });
