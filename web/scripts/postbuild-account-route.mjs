import { copyFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const wwwroot = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'server', 'Aion.Host', 'wwwroot');
const accountDir = join(wwwroot, 'account');
mkdirSync(accountDir, { recursive: true });
copyFileSync(join(wwwroot, 'account.html'), join(accountDir, 'index.html'));
