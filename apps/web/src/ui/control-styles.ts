export const controlStyles = `
:host{display:inline-flex;min-width:0;vertical-align:middle;color:var(--nx-color-text,#e9edf5);font:inherit}
*{box-sizing:border-box;scrollbar-width:thin;scrollbar-color:var(--border-strong) transparent}
button,input,textarea{appearance:none;font:inherit;color:inherit}input[type=number]{appearance:textfield}input::-webkit-inner-spin-button,input::-webkit-outer-spin-button{appearance:none;margin:0}
input,textarea{font-size:var(--nx-input-font-size,13px);font-weight:var(--nx-input-weight,400)}
input::placeholder,textarea::placeholder{color:var(--faint,var(--nx-color-muted));opacity:1}
button{font-size:13px;font-weight:650;line-height:1;min-height:var(--nx-action-height,var(--nx-control-height,40px));padding:0 15px;border:1px solid var(--nx-color-border,#394152);
border-radius:var(--nx-button-radius,var(--nx-radius-sm));background:var(--content-control,#ffffff08);cursor:pointer;
transition:background var(--nx-motion-duration-fast,.16s) ease,border-color var(--nx-motion-duration-fast,.16s) ease;
  box-shadow: var(--nx-surface-shadow);}
button:hover{background:var(--content-control-hover,#ffffff0a)}
button[data-tone=info]{border-color:var(--accent-strong,var(--nx-color-accent));background:var(--accent-strong,var(--nx-color-accent));color:white}
button[data-tone=success]{color:var(--success,#6bcf9b)}
button[data-tone=warning]{color:var(--warning,#e9b66b)}
button[data-tone=danger]{color:var(--nx-color-danger,#ef7070)}
button:disabled,input:disabled{opacity:.55;cursor:not-allowed}
button:focus-visible,input:focus,textarea:focus{outline:2px solid var(--nx-color-accent,#80a7ff);outline-offset:-2px}
input[aria-invalid=true],textarea[aria-invalid=true]{border-color:var(--nx-color-danger,#ef7070);outline-color:var(--nx-color-danger,#ef7070)}
@media(prefers-reduced-motion:reduce){*{transition:none!important;animation:none!important}}
`;

export const compositeFieldStyles = `
:host{border-radius:var(--nx-radius-sm)}
:host(:focus-within){outline:2px solid var(--nx-color-accent,#80a7ff);outline-offset:-2px}
:host([aria-invalid=true]){outline:1px solid var(--nx-color-danger,#ef7070);outline-offset:-1px}
:host([aria-invalid=true]:focus-within){outline-width:2px;outline-offset:-2px}
:host([disabled]){outline:none}
input:focus,textarea:focus{outline:none}
input[aria-invalid=true],textarea[aria-invalid=true]{border-color:var(--nx-color-border,#394152)}
`;

export const fieldStyles =
  controlStyles +
  `
:host{display:inline-flex;min-width:0;width:100%}input,textarea{width:100%;min-height:var(--nx-control-height,40px);padding:8px 12px;
border:1px solid var(--nx-color-border,#394152);border-radius:var(--nx-radius-sm);background:var(--content-embedded-fill,var(--content-control,#ffffff08));
  box-shadow: var(--nx-surface-shadow);}
textarea{resize:none;line-height:var(--nx-textarea-line-height,1.5)}
` + compositeFieldStyles;
