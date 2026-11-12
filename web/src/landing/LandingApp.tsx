import React, { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { api } from '../api';
import { applyDocumentLang } from '../i18n-lang';
import {
  CLASS_SLUGS,
  classes,
  detectLang,
  FEATURE_IDS,
  messages,
  navItems,
  persistLang,
  SECTION_IDS,
  type FeatureId,
  type Lang,
  type Messages,
  type SectionId,
} from './i18n';
import { useSectionPager } from './useSectionPager';
import { FeatherLayer } from './FeatherLayer';
import { LandingPreloader } from './LandingPreloader';
import { BrandLogo } from './BrandLogo';

const ART = '/landing/art/';
const CLASS_DIR = '/landing/classes/';
const DISCORD = 'https://discord.gg/ySPY5xcKXQ';
const PLAY_HREF = '/account?register=1';
const ROMAN = ['I', 'II', 'III', 'IV', 'V', 'VI', 'VII', 'VIII'];

const LangContext = createContext<{ lang: Lang; setLang: (l: Lang) => void; t: Messages } | null>(null);

function useLang() {
  const ctx = useContext(LangContext);
  if (!ctx) throw new Error('LangContext missing');
  return ctx;
}

interface StatusState {
  loading: boolean;
  online: boolean;
  registrationOpen: boolean;
  launcherAvailable: boolean;
  launcherVersion: string | null;
  gameReady: boolean;
}

type WorldSide = 'elyos' | 'asmo';

export function LandingApp() {
  const [lang, setLangState] = useState<Lang>(() => detectLang());
  const [menuOpen, setMenuOpen] = useState(false);
  const [loreTab, setLoreTab] = useState(0);
  const [featureTab, setFeatureTab] = useState<FeatureId>('combat');
  const [worldTab, setWorldTab] = useState<WorldSide>('elyos');
  const [classIndex, setClassIndex] = useState(0);

  const setLang = useCallback((l: Lang) => {
    persistLang(l);
    setLangState(l);
  }, []);

  useEffect(() => { applyDocumentLang(lang); }, [lang]);

  useEffect(() => {
    if (!menuOpen) return;
    const onKey = (e: globalThis.KeyboardEvent) => { if (e.key === 'Escape') setMenuOpen(false); };
    const prev = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    window.addEventListener('keydown', onKey);
    return () => {
      document.body.style.overflow = prev;
      window.removeEventListener('keydown', onKey);
    };
  }, [menuOpen]);

  const t = useMemo(() => messages(lang), [lang]);
  const nav = useMemo(() => navItems(t), [t]);
  const classCards = classes[lang];
  const { activeIndex, goTo, scrollerRef, snapMode, trackStyle, animating } =
    useSectionPager(SECTION_IDS, { disabled: menuOpen });
  const status = useServerStatus();

  // Snap mode: after a page change, move focus to the new section heading so keyboard and
  // screen-reader users land where the eye does (inactive slides are inert).
  const firstRender = useRef(true);
  useEffect(() => {
    if (firstRender.current) { firstRender.current = false; return; }
    if (!snapMode) return;
    document.getElementById(`${SECTION_IDS[activeIndex]}-heading`)?.focus({ preventScroll: true });
  }, [activeIndex, snapMode]);

  const jump = (id: SectionId) => {
    setMenuOpen(false);
    goTo(SECTION_IDS.indexOf(id));
  };

  const backgrounds = useMemo(
    () => SECTION_IDS.map(id => sectionArt(id, featureTab, worldTab)),
    [featureTab, worldTab],
  );

  // Only the active section and its neighbours (plus the one being left) carry art, so the
  // first visit downloads 2–3 backgrounds instead of all of them.
  const [leaving, setLeaving] = useState(0);
  const previous = useRef(0);
  useEffect(() => {
    setLeaving(previous.current);
    previous.current = activeIndex;
  }, [activeIndex]);
  const wantsArt = (idx: number) => Math.abs(idx - activeIndex) <= 1 || idx === leaving;

  const slide = (id: SectionId) => {
    const index = SECTION_IDS.indexOf(id);
    return { id, index, active: index === activeIndex, snapMode, bg: backgrounds[index] };
  };

  return (
    <LangContext.Provider value={{ lang, setLang, t }}>
      <LandingPreloader t={t} />
      <FeatherLayer />
      <div
        className={`landing ${snapMode ? 'landing--snap' : 'landing--flow'}${animating ? ' is-animating' : ''}`}
        data-section={SECTION_IDS[activeIndex]}
        ref={scrollerRef}
      >
        {snapMode && (
          <div className="landing-bg-stage" aria-hidden="true">
            {SECTION_IDS.map((id, idx) => (
              <div key={id} className={`landing-bg-layer${idx === activeIndex ? ' is-active' : ''}`}>
                {wantsArt(idx) && <ArtLayer name={backgrounds[idx]} />}
              </div>
            ))}
            <div className="landing-rays" />
          </div>
        )}

        <header className="landing-header">
          <button
            type="button"
            className="landing-menu-toggle"
            aria-expanded={menuOpen}
            aria-controls="landing-sidebar"
            onClick={() => setMenuOpen(true)}
          >
            <span className="landing-burger" aria-hidden="true"><span /></span>
            <span className="landing-menu-label">{t.navMenu}</span>
          </button>
          <a className="landing-header-brand" href="/" aria-label={t.brand}>
            <BrandLogo />
          </a>
          <div className="landing-header-actions">
            <a className="landing-header-account" href="/account">{t.ctaAccount}</a>
            <LangToggle />
          </div>
        </header>

        <div
          className={`landing-menu-backdrop${menuOpen ? ' is-open' : ''}`}
          aria-hidden="true"
          onClick={() => setMenuOpen(false)}
        />
        <aside
          className={`landing-sidebar${menuOpen ? ' is-open' : ''}`}
          id="landing-sidebar"
          aria-label={t.navSectionsAria}
          inert={!menuOpen}
        >
          <div className="landing-sidebar-head">
            <BrandLogo className="landing-brand-logo--sidebar" />
            <button type="button" className="landing-sidebar-close" aria-label={t.navCloseMenu} onClick={() => setMenuOpen(false)}>
              <span aria-hidden="true">×</span>
            </button>
          </div>
          <nav className="landing-sidebar-nav">
            {nav.map((item, i) => {
              const idx = SECTION_IDS.indexOf(item.id);
              return (
                <a
                  key={item.id}
                  href={`#${item.id}`}
                  className={`landing-sidebar-link${idx === activeIndex ? ' is-active' : ''}`}
                  aria-current={idx === activeIndex ? 'true' : undefined}
                  onClick={e => { e.preventDefault(); jump(item.id); }}
                >
                  <span className="landing-sidebar-num" aria-hidden="true">{String(i + 1).padStart(2, '0')}</span>
                  {item.label}
                </a>
              );
            })}
          </nav>
          <div className="landing-sidebar-foot">
            <LangToggle wide />
            <a className="landing-btn landing-btn--ghost" href="/account">{t.ctaAccount}</a>
          </div>
        </aside>

        {snapMode && (
          <>
            <a className="landing-play-dock" href={PLAY_HREF}>
              <Chevrons />
              <span className="landing-play-dock-label">{t.ctaPlayShort}</span>
              <Chevrons flip />
            </a>
            <nav className="landing-bottom-nav" aria-label={t.navSectionsAria}>
              {nav.map((item, i) => {
                const idx = SECTION_IDS.indexOf(item.id);
                return (
                  <React.Fragment key={item.id}>
                    {i > 0 && <span className="landing-bottom-nav-dash" aria-hidden="true" />}
                    <a
                      href={`#${item.id}`}
                      className={idx === activeIndex ? 'is-active' : undefined}
                      aria-current={idx === activeIndex ? 'true' : undefined}
                      onClick={e => { e.preventDefault(); jump(item.id); }}
                    >
                      {item.label}
                    </a>
                  </React.Fragment>
                );
              })}
            </nav>
          </>
        )}

        {!snapMode && (
          <div className="landing-mobile-bar">
            <a className="landing-btn landing-btn--primary" href={PLAY_HREF}>{t.ctaPlay}</a>
            <a className="landing-btn landing-btn--ghost" href={DISCORD} target="_blank" rel="noopener noreferrer">Discord</a>
          </div>
        )}

        <main className="landing-track" style={trackStyle}>
          <Slide {...slide('hero')} variant="hero">
            <div className="landing-hero">
              <p className="landing-eyebrow">{t.heroEyebrow}</p>
              <h1 id="hero-heading" tabIndex={-1}>{t.heroTitle}</h1>
              <HeroStatus status={status} />
              <p className="landing-hero-lead">{t.heroLead}</p>
              {/* Phones get Play/Discord from the bottom bar; desktop gets the Play dock. */}
              {snapMode && (
                <div className="landing-actions">
                  <a className="landing-btn landing-btn--ghost" href={DISCORD} target="_blank" rel="noopener noreferrer">{t.ctaDiscord}</a>
                </div>
              )}
            </div>
          </Slide>

          <Slide {...slide('factions')} variant="bleed">
            <SectionHead id="factions" eyebrow={t.factionsEyebrow} title={t.factionsTitle} center />
            <div className="landing-factions">
              <Faction art="factions-elyos" tone="elyos" name={t.elyosName} tag={t.elyosTag} body={t.elyosBody} />
              <Faction art="factions-asmodians" tone="asmo" name={t.asmoName} tag={t.asmoTag} body={t.asmoBody} />
            </div>
          </Slide>

          <Slide {...slide('lore')}>
            <div className="landing-split">
              <div>
                <SectionHead id="lore" eyebrow={t.loreEyebrow} title={t.loreTitle} />
                <TabList
                  className="landing-timeline"
                  label={t.loreTitle}
                  orientation="vertical"
                  count={t.loreEras.length}
                  index={loreTab}
                  onSelect={setLoreTab}
                  idPrefix="lore"
                  render={i => (
                    <>
                      <span className="landing-timeline-num" aria-hidden="true">{ROMAN[i]}</span>
                      <span>{t.loreEras[i].title}</span>
                    </>
                  )}
                />
              </div>
              <div className="landing-panel landing-lore-panel" role="tabpanel" id="lore-panel" aria-labelledby={`lore-tab-${loreTab}`} key={loreTab}>
                <span className="landing-lore-numeral" aria-hidden="true">{ROMAN[loreTab]}</span>
                <h3>{t.loreEras[loreTab].title}</h3>
                <p>{t.loreEras[loreTab].body}</p>
              </div>
            </div>
          </Slide>

          <Slide {...slide('features')}>
            <div className="landing-features">
              <SectionHead id="features" eyebrow={t.featuresEyebrow} title={t.featuresTitle} />
              <TabList
                className="landing-feature-tabs"
                label={t.featuresTitle}
                count={FEATURE_IDS.length}
                index={FEATURE_IDS.indexOf(featureTab)}
                onSelect={i => setFeatureTab(FEATURE_IDS[i])}
                idPrefix="feature"
                render={i => t.features[FEATURE_IDS[i]].title}
              />
              <div className="landing-feature-body" role="tabpanel" id="feature-panel" aria-labelledby={`feature-tab-${FEATURE_IDS.indexOf(featureTab)}`} key={featureTab}>
                <p className="landing-lead">{t.features[featureTab].body}</p>
              </div>
              {!snapMode && <FeatureFrame name={featureTab} />}
            </div>
          </Slide>

          <Slide {...slide('classes')}>
            <div className="landing-classes">
              <div className="landing-class-info">
                <SectionHead id="classes" eyebrow={t.classesEyebrow} title={t.classesTitle} small />
                <div className="landing-class-detail" role="tabpanel" id="class-panel" aria-labelledby={`class-tab-${classIndex}`} key={`${lang}-${classIndex}`}>
                  <p className="landing-class-name">{classCards[classIndex].name}</p>
                  <p className="landing-class-role">{classCards[classIndex].role}</p>
                  <p className="landing-class-blurb">{classCards[classIndex].blurb}</p>
                </div>
                <TabList
                  className="landing-class-strip"
                  label={t.classesTitle}
                  count={classCards.length}
                  index={classIndex}
                  onSelect={setClassIndex}
                  idPrefix="class"
                  render={i => (
                    <>
                      <img src={`${CLASS_DIR}${CLASS_SLUGS[i]}-avatar.jpg`} alt="" width={128} height={128} loading="lazy" decoding="async" />
                      <span className="landing-class-strip-name">{classCards[i].name}</span>
                    </>
                  )}
                />
              </div>
              <ClassPortrait index={classIndex} />
            </div>
          </Slide>

          <Slide {...slide('world')}>
            <div className="landing-world">
              <SectionHead id="world" eyebrow={t.worldEyebrow} title={t.worldTitle} />
              <TabList
                className="landing-segmented"
                label={t.worldTitle}
                count={2}
                index={worldTab === 'elyos' ? 0 : 1}
                onSelect={i => setWorldTab(i === 0 ? 'elyos' : 'asmo')}
                idPrefix="world"
                render={i => (i === 0 ? t.worldElyos : t.worldAsmo)}
              />
              <ol className="landing-world-list" role="tabpanel" id="world-panel" aria-labelledby={`world-tab-${worldTab === 'elyos' ? 0 : 1}`} key={worldTab}>
                {(worldTab === 'elyos' ? t.elyosPlaces : t.asmoPlaces).map((place, i) => (
                  <li key={place}>
                    <span className="landing-world-num" aria-hidden="true">{String(i + 1).padStart(2, '0')}</span>
                    {place}
                  </li>
                ))}
              </ol>
            </div>
          </Slide>

          <Slide {...slide('start')} variant="start">
            <SectionHead id="start" eyebrow={t.startEyebrow} title={t.startTitle} center />
            <ol className="landing-steps">
              {t.steps.map((step, i) => (
                <li key={step.title} className="landing-panel">
                  <span className="landing-step-num" aria-hidden="true">{ROMAN[i]}</span>
                  <h3>{step.title}</h3>
                  <p>{step.body}</p>
                </li>
              ))}
            </ol>
            <div className="landing-actions landing-actions--center">
              <a className="landing-btn landing-btn--primary landing-btn--lg" href={PLAY_HREF}>{t.ctaPlay}</a>
              <a className="landing-btn landing-btn--ghost landing-btn--lg" href={DISCORD} target="_blank" rel="noopener noreferrer">{t.ctaDiscord}</a>
            </div>
            <StatusList status={status} />
            <footer className="landing-footer">
              <nav className="landing-footer-links" aria-label={t.brand}>
                <a href="/account">{t.footerAccount}</a>
                <a href={DISCORD} target="_blank" rel="noopener noreferrer">Discord</a>
              </nav>
              <p>{t.footerDisclaimer}</p>
            </footer>
          </Slide>
        </main>
      </div>
    </LangContext.Provider>
  );
}

function useServerStatus(): StatusState {
  const [status, setStatus] = useState<StatusState>({
    loading: true, online: false, registrationOpen: false, launcherAvailable: false, launcherVersion: null, gameReady: false,
  });
  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const s = await api<{ accountsConfigured: boolean; registrationOpen?: boolean; launcherAvailable?: boolean; gameReady?: boolean }>('/api/status');
        let version: string | null = null;
        if (s.launcherAvailable) {
          try {
            version = (await api<{ version?: string | null }>('/api/downloads/launcher/manifest')).version ?? null;
          } catch { /* the version is optional */ }
        }
        if (!cancelled) {
          setStatus({
            loading: false,
            online: Boolean(s.accountsConfigured),
            registrationOpen: Boolean(s.registrationOpen),
            launcherAvailable: Boolean(s.launcherAvailable),
            launcherVersion: version,
            gameReady: Boolean(s.gameReady),
          });
        }
      } catch {
        if (!cancelled) setStatus(prev => ({ ...prev, loading: false, online: false, gameReady: false }));
      }
    }
    load();
    const timer = window.setInterval(load, 60_000);
    return () => { cancelled = true; window.clearInterval(timer); };
  }, []);
  return status;
}

function Slide({ id, index, active, snapMode, variant, bg, children }: {
  id: SectionId;
  index: number;
  active: boolean;
  snapMode: boolean;
  variant?: 'hero' | 'bleed' | 'start';
  bg: string;
  children: ReactNode;
}) {
  return (
    <section
      id={id}
      className={`landing-slide${active ? ' is-active' : ''}${variant ? ` landing-slide--${variant}` : ''}`}
      aria-labelledby={`${id}-heading`}
      data-index={index}
      inert={snapMode && !active}
    >
      {!snapMode && (
        <div className="landing-slide-bg" aria-hidden="true">
          <ArtLayer name={bg} />
        </div>
      )}
      <div className="landing-slide-content">{children}</div>
    </section>
  );
}

function SectionHead({ id, eyebrow, title, center, small }: { id: SectionId; eyebrow: string; title: string; center?: boolean; small?: boolean }) {
  return (
    <header className={`landing-section-head${center ? ' is-center' : ''}${small ? ' is-small' : ''}`}>
      <p className="landing-eyebrow">{eyebrow}</p>
      <h2 id={`${id}-heading`} tabIndex={-1}>{title}</h2>
    </header>
  );
}

/** WAI-ARIA tabs: one tab stop, arrows / Home / End move the selection and focus. */
function TabList({ className, label, count, index, onSelect, idPrefix, render, orientation = 'horizontal' }: {
  className: string;
  label: string;
  count: number;
  index: number;
  onSelect: (i: number) => void;
  idPrefix: string;
  render: (i: number) => ReactNode;
  orientation?: 'horizontal' | 'vertical';
}) {
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const prev = orientation === 'vertical' ? 'ArrowUp' : 'ArrowLeft';
    const next = orientation === 'vertical' ? 'ArrowDown' : 'ArrowRight';
    let target = -1;
    if (e.key === prev) target = (index - 1 + count) % count;
    else if (e.key === next) target = (index + 1) % count;
    else if (e.key === 'Home') target = 0;
    else if (e.key === 'End') target = count - 1;
    if (target < 0) return;
    e.preventDefault();
    onSelect(target);
    e.currentTarget.querySelectorAll<HTMLElement>('[role="tab"]')[target]?.focus();
  };
  return (
    <div className={className} role="tablist" aria-label={label} aria-orientation={orientation} onKeyDown={onKeyDown}>
      {Array.from({ length: count }, (_, i) => (
        <button
          key={i}
          type="button"
          role="tab"
          id={`${idPrefix}-tab-${i}`}
          aria-selected={i === index}
          aria-controls={`${idPrefix}-panel`}
          tabIndex={i === index ? 0 : -1}
          className={i === index ? 'is-active' : undefined}
          onClick={() => onSelect(i)}
        >
          {render(i)}
        </button>
      ))}
    </div>
  );
}

function Faction({ art, tone, name, tag, body }: { art: string; tone: 'elyos' | 'asmo'; name: string; tag: string; body: string }) {
  return (
    <article className={`landing-faction landing-faction--${tone}`}>
      <ArtLayer name={art} className="landing-faction-art" />
      <div className="landing-faction-copy">
        <span className="landing-faction-emblem" aria-hidden="true" />
        <p className="landing-faction-tag">{tag}</p>
        <h3>{name}</h3>
        <p className="landing-faction-body">{body}</p>
      </div>
    </article>
  );
}

function ClassPortrait({ index }: { index: number }) {
  const slug = CLASS_SLUGS[index];
  // Warm the cache for the neighbours so switching feels instant.
  useEffect(() => {
    const narrow = window.matchMedia('(max-width: 899px)').matches;
    for (const n of [index - 1, index + 1]) {
      const s = CLASS_SLUGS[(n + CLASS_SLUGS.length) % CLASS_SLUGS.length];
      new Image().src = `${CLASS_DIR}${s}${narrow ? '-m' : ''}.webp`;
    }
  }, [index]);
  return (
    <div className="landing-class-portrait" aria-hidden="true">
      <picture key={slug}>
        <source media="(max-width: 899px)" srcSet={`${CLASS_DIR}${slug}-m.webp`} />
        <img src={`${CLASS_DIR}${slug}.webp`} alt="" decoding="async" />
      </picture>
    </div>
  );
}

function FeatureFrame({ name }: { name: string }) {
  return (
    <div className="landing-feature-frame" aria-hidden="true">
      <picture key={name}>
        <source media="(max-width: 899px)" srcSet={`${ART}${name}-m.webp`} />
        <img src={`${ART}${name}.webp`} alt="" loading="lazy" decoding="async" />
      </picture>
    </div>
  );
}

function ArtLayer({ name, className = 'landing-art' }: { name: string; className?: string }) {
  return <div className={className} style={artVars(name)} />;
}

function LangToggle({ wide }: { wide?: boolean }) {
  const { lang, setLang, t } = useLang();
  return (
    <div className={`landing-lang${wide ? ' landing-lang--wide' : ''}`} role="group" aria-label={t.langAria}>
      {(['en', 'ru'] as const).map(l => (
        <button key={l} type="button" className={lang === l ? 'is-active' : undefined} aria-pressed={lang === l} onClick={() => setLang(l)}>
          {l.toUpperCase()}
        </button>
      ))}
    </div>
  );
}

/** Gold "»»»" chevrons (the chanage.online play-button motif). */
function Chevrons({ flip }: { flip?: boolean }) {
  return (
    <svg className={`landing-chevrons${flip ? ' is-flipped' : ''}`} viewBox="0 0 66 24" aria-hidden="true">
      <path d="M2 0 L14 12 L2 24 H12 L24 12 L12 0 Z" />
      <path d="M22 0 L34 12 L22 24 H32 L44 12 L32 0 Z" />
      <path d="M42 0 L54 12 L42 24 H52 L64 12 L52 0 Z" />
    </svg>
  );
}

/** Server state under the hero title: "Registration open »»»" / "Server offline". */
function HeroStatus({ status }: { status: StatusState }) {
  const { t } = useLang();
  const tone = status.loading ? 'pending' : status.online ? 'ok' : 'down';
  const text = status.loading
    ? t.loading
    : !status.online ? t.heroStatusOffline
    : status.registrationOpen ? t.heroStatusOpen : t.heroStatusOnline;
  return (
    <p className={`landing-hero-status is-${tone}`} role="status">
      <span>{text}</span>
      {tone === 'ok' && <Chevrons />}
    </p>
  );
}

function StatusList({ status }: { status: StatusState }) {
  const { t } = useLang();
  const rows: { label: string; value: string; ok: boolean }[] = [
    { label: t.statusOnline, value: status.online ? t.online : t.offline, ok: status.online },
    { label: t.statusReg, value: status.registrationOpen ? t.open : t.closed, ok: status.registrationOpen },
    {
      label: t.statusLauncher,
      value: status.launcherAvailable ? (status.launcherVersion ? `v${status.launcherVersion}` : t.available) : t.unavailable,
      ok: status.launcherAvailable,
    },
    { label: t.statusGame, value: status.gameReady ? t.online : t.offline, ok: status.gameReady },
  ];
  return (
    <section className="landing-status" aria-label={t.statusTitle}>
      <dl>
        {rows.map(row => (
          <div key={row.label} className={status.loading ? 'is-pending' : row.ok ? 'is-ok' : 'is-down'}>
            <dt>{row.label}</dt>
            <dd>{status.loading ? t.loading : row.value}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

export function legacyAccountRedirect() {
  const hash = window.location.hash;
  if (hash === '#totp' || hash === '#security') {
    window.location.replace('/account' + window.location.search + hash);
  }
}

function sectionArt(id: SectionId, feature: FeatureId, world: WorldSide): string {
  switch (id) {
    case 'hero': return 'hero';
    case 'factions': return 'lore';
    case 'lore': return 'lore';
    case 'features': return feature;
    case 'classes': return 'dungeons';
    case 'world': return world === 'elyos' ? 'world-elyos' : 'world-asmodian';
    case 'start': return 'flight';
  }
}

// Custom properties are set through the CSSOM (allowed by the page's style-src 'self' CSP).
function artVars(name: string): React.CSSProperties {
  return {
    ['--art' as string]: `url(${ART}${name}.webp)`,
    ['--art-m' as string]: `url(${ART}${name}-m.webp)`,
  } as React.CSSProperties;
}
