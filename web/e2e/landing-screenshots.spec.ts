import { test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const outDir = join(import.meta.dirname, '..', '..', 'artifacts', 'screenshots');

test.beforeAll(() => {
  mkdirSync(outDir, { recursive: true });
});

test('landing page screenshot', async ({ page }, testInfo) => {
  await page.goto('/');
  await page.waitForSelector('#hero-heading');
  await page.waitForTimeout(500);
  const name = `landing-${testInfo.project.name}.png`;
  await page.screenshot({ path: join(outDir, name), fullPage: true });
});
