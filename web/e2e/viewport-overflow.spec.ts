import { test, expect } from '@playwright/test';

const NARROW_VIEWPORTS = [
  { width: 360, height: 800 },
  { width: 390, height: 844 },
] as const;

const LANGS = ['en', 'ru'] as const;

async function prepLanding(page: import('@playwright/test').Page, lang: string) {
  await page.addInitScript(l => {
    localStorage.setItem('chanage-lang', l);
  }, lang);
  await page.goto('/');
  await page.waitForSelector('.landing-loader.is-hidden, #hero-heading', { timeout: 60_000 });
  await page.waitForTimeout(300);
}

async function assertNoHorizontalOverflow(page: import('@playwright/test').Page) {
  const result = await page.evaluate(() => {
    const viewportWidth = document.documentElement.clientWidth;
    const offenders: { tag: string; id: string; className: string; right: number; width: number }[] = [];
    for (const el of document.querySelectorAll<HTMLElement>('.landing *')) {
      const rect = el.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) continue;
      if (rect.right > viewportWidth + 1.5) {
        offenders.push({
          tag: el.tagName,
          id: el.id,
          className: el.className.slice(0, 80),
          right: Math.round(rect.right),
          width: Math.round(rect.width),
        });
      }
    }
    const docScroll = document.documentElement.scrollWidth;
    return { viewportWidth, docScroll, offenders: offenders.slice(0, 12) };
  });
  expect(
    result.docScroll,
    `document scrollWidth ${result.docScroll} > viewport ${result.viewportWidth} (${JSON.stringify(result.offenders)})`,
  ).toBeLessThanOrEqual(result.viewportWidth + 1);
}

for (const vp of NARROW_VIEWPORTS) {
  for (const lang of LANGS) {
    test(`landing has no horizontal overflow ${vp.width}x${vp.height} ${lang}`, async ({ page }) => {
      await page.setViewportSize(vp);
      await prepLanding(page, lang);
      await page.locator('#lore').scrollIntoViewIfNeeded();
      await page.waitForTimeout(200);
      await assertNoHorizontalOverflow(page);
    });

    test(`account has no horizontal overflow ${vp.width}x${vp.height} ${lang}`, async ({ page }) => {
      await page.setViewportSize(vp);
      await page.addInitScript(l => {
        localStorage.setItem('chanage-lang', l);
      }, lang);
      await page.goto('/account/');
      await page.waitForSelector('.account-card', { timeout: 60_000 });
      await assertNoHorizontalOverflow(page);
    });
  }
}