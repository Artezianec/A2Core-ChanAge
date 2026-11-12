import { useEffect, useState } from 'react';
import type { Messages } from './i18n';
import { BrandLogo } from './BrandLogo';

const SEEN_KEY = 'chanage-intro-seen';
const MIN_VISIBLE_MS = 450;
const MAX_VISIBLE_MS = 1600;

function shouldShow(): boolean {
  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return false;
  try { return !sessionStorage.getItem(SEEN_KEY); } catch { return true; }
}

/** Intro curtain on the first visit of a session: stays only until the hero art is decoded (capped). */
export function LandingPreloader({ t }: { t: Messages }) {
  const [show] = useState(shouldShow);
  const [hidden, setHidden] = useState(!show);
  const [gone, setGone] = useState(!show);

  useEffect(() => {
    if (!show) return;
    try { sessionStorage.setItem(SEEN_KEY, '1'); } catch { /* storage disabled */ }
    const start = performance.now();
    let done = false;
    let wait = 0;
    const finish = () => {
      if (done) return;
      done = true;
      wait = window.setTimeout(() => setHidden(true), Math.max(0, MIN_VISIBLE_MS - (performance.now() - start)));
    };
    const hero = new Image();
    hero.src = window.matchMedia('(max-width: 899px)').matches ? '/landing/art/hero-m.webp' : '/landing/art/hero.webp';
    hero.decode().then(finish, finish);
    const cap = window.setTimeout(finish, MAX_VISIBLE_MS);
    return () => {
      done = true;
      window.clearTimeout(cap);
      window.clearTimeout(wait);
    };
  }, [show]);

  if (gone) return null;
  return (
    <div
      className={`landing-loader${hidden ? ' is-hidden' : ''}`}
      onTransitionEnd={e => { if (hidden && e.target === e.currentTarget) setGone(true); }}
      role="status"
      aria-label={t.loaderAria}
    >
      <div className="landing-loader-content" aria-hidden="true">
        <BrandLogo className="landing-brand-logo--loader" />
        <span className="landing-loader-subtitle">{t.loaderSubtitle}</span>
        <span className="landing-loader-progress"><span /></span>
      </div>
    </div>
  );
}
