import { LitElement, html, css } from '../lib/lit-all.min.js';

export class X13LogoDocument extends LitElement {
  static properties = {
    revealedSegments: { state: true },
  };

  static styles = css`
    :host { display: block; }
    .segment-wheel {
      position: relative;
      width: 70vh;
      height: 70vh;
    }
    .segment-wheel img {
      position: absolute;
      top: 0;
      left: 0;
      width: 70vh;
      height: 70vh;
      opacity: 0;
      transition: opacity 0.3s ease;
    }
    .segment-wheel img.visible {
      opacity: 0.7;
    }
  `;

  constructor() {
    super();
    this.revealedSegments = 0;
  }

  connectedCallback() {
    super.connectedCallback();
    this.#startSegmentReveal();
  }

  render() {
    return html`
      <div class="segment-wheel">
        ${[0, 1, 2, 3, 4, 5].map((i) => html`
          <img src="/ide_icons/segment.svg" style="transform: rotate(${360 - i * 60}deg)" class=${i < this.revealedSegments ? 'visible' : ''}>
        `)}
      </div>`;
  }

  #segmentsTimerStarted = false;

  #startSegmentReveal() {
    if(this.#segmentsTimerStarted) return;
    this.#segmentsTimerStarted = true;
    this.revealedSegments = 1;
    let delay = 300;
    let elapsed = 0;
    for(let i = 1; i < 6; i++) {
      elapsed += delay;
      setTimeout(() => {
        this.revealedSegments = i + 1;
      }, elapsed);
      delay -= 25;
    }
  }
}

customElements.define('x13-logo-document', X13LogoDocument);
