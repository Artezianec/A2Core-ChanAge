import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { legacyAccountRedirect, LandingApp } from './LandingApp';
import './styles.css';

legacyAccountRedirect();

createRoot(document.getElementById('root')!).render(
  <StrictMode><LandingApp /></StrictMode>,
);
