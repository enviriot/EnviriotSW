// Раскладка оболочки IDE: ширина Workspace, высота и свёрнутость журнала - с перетаскиванием
// разделителей и запоминанием в localStorage. ReactiveController: размеры рисует хост, а
// перерисовку при их изменении просит контроллер.
import { clamp, readPositiveNumber, readBoolean } from '../services/local-storage-utils.js';

const WORKSPACE_WIDTH_KEY = 'x13.workspace.width';
const DEFAULT_WORKSPACE_WIDTH = 420;
const MIN_WORKSPACE_WIDTH = 260;
const MAX_WORKSPACE_WIDTH = 900;

const LOG_HEIGHT_KEY = 'x13.logpanel.height';
const LOG_COLLAPSED_KEY = 'x13.logpanel.collapsed';
const DEFAULT_LOG_HEIGHT = 220;
const MIN_LOG_HEIGHT = 80;
const MAX_LOG_HEIGHT = 700;
const COLLAPSED_LOG_HEIGHT = 28;

export class ShellLayout {
  constructor(host) {
    this.host = host;
    this.workspaceWidth = readPositiveNumber(WORKSPACE_WIDTH_KEY, DEFAULT_WORKSPACE_WIDTH);
    this.logHeight = readPositiveNumber(LOG_HEIGHT_KEY, DEFAULT_LOG_HEIGHT);
    this.logCollapsed = readBoolean(LOG_COLLAPSED_KEY, true);
    host.addController(this);
  }

  // Высота, которую журнал занимает на экране: свёрнутый - только заголовок.
  get logPaneHeight() {
    return this.logCollapsed ? COLLAPSED_LOG_HEIGHT : this.logHeight;
  }

  startResize(e) {
    e.preventDefault();
    const startX = e.clientX;
    const startWidth = this.workspaceWidth;
    const move = (ev) => {
      this.workspaceWidth = clamp(startWidth - (ev.clientX - startX), MIN_WORKSPACE_WIDTH, MAX_WORKSPACE_WIDTH);
      this.host.requestUpdate();
    };
    const up = () => {
      localStorage.setItem(WORKSPACE_WIDTH_KEY, String(this.workspaceWidth));
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  }

  startLogResize(e) {
    if(this.logCollapsed) return;
    e.preventDefault();
    const startY = e.clientY;
    const startHeight = this.logHeight;
    const move = (ev) => {
      this.logHeight = clamp(startHeight - (ev.clientY - startY), MIN_LOG_HEIGHT, MAX_LOG_HEIGHT);
      this.host.requestUpdate();
    };
    const up = () => {
      localStorage.setItem(LOG_HEIGHT_KEY, String(this.logHeight));
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up, { once: true });
  }

  toggleLogCollapsed() {
    this.logCollapsed = !this.logCollapsed;
    localStorage.setItem(LOG_COLLAPSED_KEY, String(this.logCollapsed));
    this.host.requestUpdate();
  }
}
