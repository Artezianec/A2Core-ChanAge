import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const VIEWPORTS = [
  { tag: '360', width: 360, height: 740 },
  { tag: '390', width: 390, height: 844 },
  { tag: '768', width: 768, height: 1024 },
  { tag: '1366', width: 1366, height: 768 },
] as const;

const LANGS = ['en', 'ru'] as const;
const SECTIONS = ['hero', 'factions', 'lore', 'features', 'classes', 'world', 'start'] as const;

const outDir = join(import.meta.dirname, '..', '..', 'artifacts', 'screenshots', 'responsive');

test.beforeAll(() => {
  mkdirSync(outDir, { recursive: true });
});

async function setLang(page: import('@playwright/test').Page, lang: string) {
  await page.addInitScript(l => {
    localStorage.setItem('chanage-lang', l);
  }, lang);
}

async function scrollThroughLanding(page: import('@playwright/test').Page) {
  await page.goto('/');
  await page.waitForSelector('.landing-loader.is-hidden, #hero-heading', { timeout: 60_000 });
  await page.waitForTimeout(400);
  for (const id of SECTIONS) {
    await page.locator(`#${id}`).scrollIntoViewIfNeeded();
    await page.waitForTimeout(250);
  }
  await page.locator('#hero').scrollIntoViewIfNeeded();
}

for (const vp of VIEWPORTS) {
  for (const lang of LANGS) {
    test(`screenshot landing ${vp.tag} ${lang}`, async ({ page }) => {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await setLang(page, lang);
      await scrollThroughLanding(page);
      const name = `landing-${vp.tag}-${lang}.png`;
      await page.screenshot({ path: join(outDir, name), fullPage: true });
    });

    test(`screenshot account ${vp.tag} ${lang}`, async ({ page }) => {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await setLang(page, lang);
      await page.goto('/account/');
      await page.waitForSelector('.account-card', { timeout: 60_000 });
      await page.waitForTimeout(300);
      const name = `account-${vp.tag}-${lang}.png`;
      await page.screenshot({ path: join(outDir, name), fullPage: true });
    });
  }
}

/** Landscape phone: content fits without clipping the fixed mobile bar region. */
test('landscape phone 844x390 lore section', async ({ page }) => {
  await page.setViewportSize({ width: 844, height: 390 });
  await setLang(page, 'ru');
  await scrollThroughLanding(page);
  await page.locator('#lore-panel').waitFor({ state: 'visible' });
  const clipped = await page.evaluate(() => {
    const panel = document.getElementById('lore-panel');
    if (!panel) return true;
    return panel.scrollWidth > panel.clientWidth + 2;
  });
  expect(clipped).toBe(false);
});