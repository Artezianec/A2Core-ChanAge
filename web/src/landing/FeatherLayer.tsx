import { useEffect, useRef } from 'react';

/** Lightweight canvas layer: drifting glowing motes and a few falling feathers. No external libs. */
export function FeatherLayer() {
  const ref = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = ref.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    let w = 0, h = 0, raf = 0;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const resize = () => {
      w = window.innerWidth; h = window.innerHeight;
      canvas.width = w * dpr; canvas.height = h * dpr;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    };
    resize();
    window.addEventListener('resize', resize);

    const mobile = w < 900;
    type P = { x: number; y: number; vx: number; vy: number; r: number; a: number; feather: boolean; rot: number; vr: number; gold: boolean };
    const make = (initial: boolean): P => {
      const feather = Math.random() < 0.12;
      return {
        x: Math.random() * w,
        y: initial ? Math.random() * h : (feather ? -20 : h + 10),
        vx: (Math.random() - 0.5) * 0.25,
        vy: feather ? 0.3 + Math.random() * 0.4 : -(0.15 + Math.random() * 0.45),
        r: feather ? 7 + Math.random() * 6 : 0.6 + Math.random() * 1.8,
        a: 0.25 + Math.random() * 0.6,
        feather,
        rot: Math.random() * Math.PI * 2,
        vr: (Math.random() - 0.5) * 0.02,
        gold: Math.random() < 0.35,
      };
    };
    const parts: P[] = Array.from({ length: mobile ? 24 : 56 }, () => make(true));

    const drawFeather = (p: P) => {
      ctx.save();
      ctx.translate(p.x, p.y);
      ctx.rotate(p.rot);
      ctx.globalAlpha = p.a * 0.7;
      const g = ctx.createLinearGradient(0, -p.r, 0, p.r);
      g.addColorStop(0, 'rgba(234,252,255,0.95)');
      g.addColorStop(1, 'rgba(120,180,255,0.15)');
      ctx.fillStyle = g;
      ctx.beginPath();
      ctx.moveTo(0, -p.r);
      ctx.quadraticCurveTo(p.r * 0.45, 0, 0, p.r);
      ctx.quadraticCurveTo(-p.r * 0.45, 0, 0, -p.r);
      ctx.fill();
      ctx.restore();
    };

    let t = 0;
    const tick = () => {
      t += 1;
      ctx.clearRect(0, 0, w, h);
      ctx.globalCompositeOperation = 'lighter';
      for (let i = 0; i < parts.length; i++) {
        const p = parts[i];
        p.x += p.vx + Math.sin((t + i * 37) / 90) * (p.feather ? 0.6 : 0.15);
        p.y += p.vy;
        p.rot += p.vr + (p.feather ? Math.sin((t + i) / 60) * 0.01 : 0);
        if (p.y < -30 || p.y > h + 30 || p.x < -30 || p.x > w + 30) { parts[i] = make(false); continue; }
        if (p.feather) { drawFeather(p); continue; }
        const flicker = 0.6 + 0.4 * Math.sin((t + i * 13) / 25);
        ctx.globalAlpha = p.a * flicker;
        ctx.fillStyle = p.gold ? '#f5d58a' : '#bfe9ff';
        ctx.shadowColor = ctx.fillStyle;
        ctx.shadowBlur = 8;
        ctx.beginPath();
        ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
        ctx.fill();
      }
      ctx.shadowBlur = 0;
      ctx.globalAlpha = 1;
      raf = requestAnimationFrame(tick);
    };
    // Pause while the tab is in the background: no point burning battery on an unseen canvas.
    const onVisibility = () => {
      cancelAnimationFrame(raf);
      if (!document.hidden && !reduce) raf = requestAnimationFrame(tick);
    };
    if (!reduce) raf = requestAnimationFrame(tick);
    document.addEventListener('visibilitychange', onVisibility);

    return () => {
      cancelAnimationFrame(raf);
      window.removeEventListener('resize', resize);
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, []);

  return <canvas ref={ref} className="landing-feathers" aria-hidden="true" />;
}
