import { BaseComponent } from '../lib/symbiote.js';

class X13_Door extends BaseComponent {
  init$ = {
    value: false,
  }
  initCallback() {
    this.sub('value', v => this.className = (v === true || v === 1 || v === 'true') ? "closed" : "open");
  }
}

X13_Door.template = /*html*/ `<hr />`;
X13_Door.rootStyles = /*css*/ `
x13-door {
  display:block;
  width:95%;
  min-height:12px;
  border-style: solid;
  border-width: 0px 3px;
  border-color: #FF4040;
}
x13-door>hr{
  width:95%;
  height:4px;
  border-width:0;
  text-align:center;
  background-color:transparent;
}
x13-door.open {
  border-color: transparent;
}
x13-door.open>hr{
  background-color:#40FF40;
  }
`;
X13_Door.bindAttributes({ value: "value"});
X13_Door.reg("x13-door");
