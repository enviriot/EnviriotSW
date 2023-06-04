import { BaseComponent } from '/lib/symbiote.js';

class X13_text extends BaseComponent {
  init$ = {
    value: false,
    text: ""
  }
}
X13_text.template = /*html*/ `
  <style>
  div { 
    display: inline
  }
  </style>
  <div> {{text}}</div >
  `;
X13_text.bindAttributes({"text":"text"});

X13_text.reg("x13-text");