// Same lockup as the ChanAge Lineage II site: server badge + game wordmark (Aion palette).
const BADGE = '/landing/chanage-badge.webp';
const WORDMARK = '/landing/aion2-wordmark.webp';

export function BrandLogo({ className }: { className?: string }) {
  return (
    <span className={`landing-brand-logo${className ? ` ${className}` : ''}`}>
      <img className="landing-brand-crest" src={BADGE} alt="ChanAge" width={320} height={320} decoding="async" />
      <img className="landing-brand-wordmark" src={WORDMARK} alt="Aion 2" width={522} height={156} decoding="async" />
    </span>
  );
}
