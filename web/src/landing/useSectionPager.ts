import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';

// Full-screen paging only when a section actually fits: wide *and* tall enough. Smaller screens
// (phones, short laptop windows) get a normal scrolling page instead of clipped slides.
const SNAP_MQ = '(min-width: 900px) and (min-height: 640px)';
const SECTION_MS = 900;
const EASE = 'cubic-bezier(0.65, 0, 0.35, 1)';
const WHEEL_THRESHOLD = 48;
const SWIPE_THRESHOLD = 56;

/** True when `target` sits in a scrollable box (below `root`) that can still scroll in direction `dy`. */
function canScrollInside(target: EventTarget | null, root: HTMLElement, dy: number): boolean {
  let el = target instanceof Element ? target : null;
  while (el && el !== root) {
    if (el instanceof HTMLElement && el.scrollHeight > el.clientHeight + 1) {
      const overflow = getComputedStyle(el).overflowY;
      if (overflow === 'auto' || overflow === 'scroll') {
        if (dy > 0 && el.scrollTop + el.clientHeight < el.scrollHeight - 1) return true;
        if (dy < 0 && el.scrollTop > 0) return true;
      }
    }
    el = el.parentElement;
  }
  return false;
}

export function useSectionPager(sectionIds: readonly string[], { disabled = false } = {}) {
  const [activeIndex, setActiveIndex] = useState(0);
  const [snapMode, setSnapMode] = useState(() => window.matchMedia(SNAP_MQ).matches);
  const [reducedMotion, setReducedMotion] = useState(() => window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const [animating, setAnimating] = useState(false);
  const scrollerRef = useRef<HTMLDivElement>(null);
  const activeRef = useRef(0);
  const lockRef = useRef(false);
  const lockTimerRef = useRef(0);
  const wheelAccumRef = useRef(0);
  const wheelIdleRef = useRef(0);
  const touchStartY = useRef<number | null>(null);
  const disabledRef = useRef(disabled);

  activeRef.current = activeIndex;
  disabledRef.current = disabled;
  const duration = reducedMotion ? 0 : SECTION_MS;

  useEffect(() => {
    const mq = window.matchMedia(SNAP_MQ);
    const rm = window.matchMedia('(prefers-reduced-motion: reduce)');
    const applyMq = () => setSnapMode(mq.matches);
    const applyRm = () => setReducedMotion(rm.matches);
    mq.addEventListener('change', applyMq);
    rm.addEventListener('change', applyRm);
    return () => {
      mq.removeEventListener('change', applyMq);
      rm.removeEventListener('change', applyRm);
    };
  }, []);

  useEffect(() => () => window.clearTimeout(lockTimerRef.current), []);

  const goTo = useCallback(
    (index: number) => {
      const next = Math.max(0, Math.min(sectionIds.length - 1, index));

      if (!snapMode) {
        document.getElementById(sectionIds[next])?.scrollIntoView({
          behavior: reducedMotion ? 'auto' : 'smooth',
          block: 'start',
        });
        setActiveIndex(next);
        return;
      }

      if (next === activeRef.current || lockRef.current) return;
      activeRef.current = next;
      setActiveIndex(next);
      if (duration > 0) {
        lockRef.current = true;
        setAnimating(true);
        window.clearTimeout(lockTimerRef.current);
        lockTimerRef.current = window.setTimeout(() => {
          lockRef.current = false;
          setAnimating(false);
        }, duration + 40);
      }
    },
    [duration, reducedMotion, sectionIds, snapMode],
  );

  const goNext = useCallback(() => goTo(activeRef.current + 1), [goTo]);
  const goPrev = useCallback(() => goTo(activeRef.current - 1), [goTo]);

  // Scrolling page: the active section is the one nearest the viewport centre.
  useEffect(() => {
    if (snapMode) return;
    let raf = 0;
    const pick = () => {
      const centre = window.innerHeight / 2;
      let best = activeRef.current;
      let bestDistance = Number.POSITIVE_INFINITY;
      sectionIds.forEach((id, i) => {
        const rect = document.getElementById(id)?.getBoundingClientRect();
        if (!rect) return;
        const distance = Math.abs(rect.top + rect.height / 2 - centre);
        if (distance < bestDistance) { bestDistance = distance; best = i; }
      });
      if (best !== activeRef.current) setActiveIndex(best);
    };
    const schedule = () => {
      window.cancelAnimationFrame(raf);
      raf = window.requestAnimationFrame(pick);
    };
    schedule();
    window.addEventListener('scroll', schedule, { passive: true });
    window.addEventListener('resize', schedule, { passive: true });
    return () => {
      window.removeEventListener('scroll', schedule);
      window.removeEventListener('resize', schedule);
      window.cancelAnimationFrame(raf);
    };
  }, [snapMode, sectionIds]);

  // Leaving snap mode (window resized): land on the section that was showing.
  useEffect(() => {
    if (!snapMode) document.getElementById(sectionIds[activeRef.current])?.scrollIntoView({ block: 'start' });
  }, [snapMode, sectionIds]);

  useEffect(() => {
    const root = scrollerRef.current;
    if (!root || !snapMode) return;

    const onWheel = (e: WheelEvent) => {
      if (disabledRef.current || e.ctrlKey) return;
      // Let a section whose content overflows scroll itself first.
      if (canScrollInside(e.target, root, e.deltaY)) return;
      e.preventDefault();
      if (lockRef.current) return;

      wheelAccumRef.current += e.deltaY;
      window.clearTimeout(wheelIdleRef.current);
      wheelIdleRef.current = window.setTimeout(() => { wheelAccumRef.current = 0; }, 140);

      const threshold = e.deltaMode === 1 ? 1 : WHEEL_THRESHOLD;
      if (Math.abs(wheelAccumRef.current) < threshold) return;
      const dir = wheelAccumRef.current > 0 ? 1 : -1;
      wheelAccumRef.current = 0;
      goTo(activeRef.current + dir);
    };

    const onKeyDown = (e: KeyboardEvent) => {
      if (disabledRef.current || lockRef.current || e.altKey || e.metaKey || e.ctrlKey) return;
      const target = e.target as HTMLElement | null;
      // Text fields and tab lists own their arrow keys.
      if (target?.closest('input, textarea, select, [contenteditable="true"], [role="tablist"]')) return;
      const onControl = Boolean(target?.closest('button, a'));

      switch (e.key) {
        case 'ArrowDown':
        case 'PageDown':
          e.preventDefault();
          goNext();
          break;
        case 'ArrowUp':
        case 'PageUp':
          e.preventDefault();
          goPrev();
          break;
        case 'Home':
          e.preventDefault();
          goTo(0);
          break;
        case 'End':
          e.preventDefault();
          goTo(sectionIds.length - 1);
          break;
        case ' ':
          if (onControl) break; // Space activates the focused button or link.
          e.preventDefault();
          if (e.shiftKey) goPrev(); else goNext();
          break;
        default:
          break;
      }
    };

    const onTouchStart = (e: TouchEvent) => {
      touchStartY.current = e.touches.length === 1 ? e.touches[0].clientY : null;
    };

    const onTouchEnd = (e: TouchEvent) => {
      const start = touchStartY.current;
      touchStartY.current = null;
      if (start == null || lockRef.current || disabledRef.current) return;
      const dy = start - e.changedTouches[0].clientY;
      if (Math.abs(dy) < SWIPE_THRESHOLD || canScrollInside(e.target, root, dy)) return;
      goTo(activeRef.current + (dy > 0 ? 1 : -1));
    };

    root.addEventListener('wheel', onWheel, { passive: false });
    window.addEventListener('keydown', onKeyDown);
    root.addEventListener('touchstart', onTouchStart, { passive: true });
    root.addEventListener('touchend', onTouchEnd, { passive: true });

    return () => {
      root.removeEventListener('wheel', onWheel);
      window.removeEventListener('keydown', onKeyDown);
      root.removeEventListener('touchstart', onTouchStart);
      root.removeEventListener('touchend', onTouchEnd);
      window.clearTimeout(wheelIdleRef.current);
    };
  }, [goNext, goPrev, goTo, sectionIds.length, snapMode]);

  const trackStyle: CSSProperties | undefined = snapMode
    ? {
        transform: `translate3d(0, ${-activeIndex * 100}vh, 0)`,
        transition: animating && duration > 0 ? `transform ${duration}ms ${EASE}` : undefined,
      }
    : undefined;

  return {
    activeIndex,
    goTo,
    goNext,
    goPrev,
    scrollerRef,
    snapMode,
    sectionCount: sectionIds.length,
    trackStyle,
    animating,
    reducedMotion,
  };
}
