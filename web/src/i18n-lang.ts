// UI language shared by the landing and the account page (one stored choice for both).
export type Lang = 'en' | 'ru';

const KEY = 'chanage-lang';

export function detectLang(): Lang {
  try {
    const stored = localStorage.getItem(KEY);
    if (stored === 'en' || stored === 'ru') return stored;
  } catch { /* storage disabled */ }
  if (typeof navigator !== 'undefined') {
    const langs = navigator.languages?.length ? navigator.languages : [navigator.language];
    if (langs.some(l => l?.toLowerCase().startsWith('ru'))) return 'ru';
  }
  return 'en';
}

export function persistLang(lang: Lang) {
  try { localStorage.setItem(KEY, lang); } catch { /* storage disabled */ }
}

/** Keeps <html lang> in sync: screen readers and the :lang(ru) display font depend on it. */
export function applyDocumentLang(lang: Lang) {
  document.documentElement.lang = lang;
}
