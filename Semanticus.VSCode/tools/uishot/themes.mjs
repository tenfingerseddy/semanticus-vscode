// The three VS Code themes the screen check renders every page in. VS Code gives a webview its theme as
// --vscode-* variables on <html> and a class on <body>; these are the real values of Dark Modern, Light Modern
// and Dark High Contrast, the same ones the Instrument look-and-feel frames were drawn with (23 September 2026).
// The harness sets none of them, so without this a page is only ever seen on the tokens' fallbacks.
// The --vscode-charts-* values (colour level B, 24 September 2026) are what VS Code 1.136 registers for these
// themes: none of the three theme files overrides them, so they are the colour registry's defaults. charts.orange
// is the find-match highlight, which ships see-through and is unset in high contrast, so Dark High Contrast
// leaves it out, exactly as VS Code does. test/token-contrast.test.mjs checks that each theme carries them.
const UI_FONT = '"Segoe WPC", "Segoe UI", system-ui, sans-serif';
const EDITOR_FONT = 'Consolas, "Courier New", monospace';

export const THEMES = {
  dark: {
    label: 'Dark Modern', bodyClass: 'vscode-dark',
    vars: {
      '--vscode-font-family': UI_FONT, '--vscode-editor-font-family': EDITOR_FONT,
      '--vscode-editor-background': '#1F1F1F', '--vscode-foreground': '#CCCCCC', '--vscode-descriptionForeground': '#9D9D9D',
      '--vscode-focusBorder': '#0078D4', '--vscode-button-background': '#0078D4', '--vscode-button-foreground': '#FFFFFF',
      '--vscode-button-secondaryBackground': '#313131', '--vscode-button-secondaryForeground': '#CCCCCC',
      '--vscode-panel-border': '#2B2B2B', '--vscode-input-background': '#313131', '--vscode-input-border': '#3C3C3C',
      '--vscode-input-foreground': '#CCCCCC', '--vscode-input-placeholderForeground': '#989898', '--vscode-textLink-foreground': '#4DAAFC',
      '--vscode-errorForeground': '#F85149', '--vscode-editorWarning-foreground': '#CCA700',
      '--vscode-charts-green': '#89D185', '--vscode-charts-yellow': '#CCA700', '--vscode-charts-red': '#F14C4C',
      '--vscode-charts-blue': '#59A4F9', '--vscode-charts-orange': '#EA5C0055', '--vscode-charts-purple': '#B180D7',
      '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent',
    },
  },
  light: {
    label: 'Light Modern', bodyClass: 'vscode-light',
    vars: {
      '--vscode-font-family': UI_FONT, '--vscode-editor-font-family': EDITOR_FONT,
      '--vscode-editor-background': '#FFFFFF', '--vscode-foreground': '#3B3B3B', '--vscode-descriptionForeground': '#3B3B3B',
      '--vscode-focusBorder': '#005FB8', '--vscode-button-background': '#005FB8', '--vscode-button-foreground': '#FFFFFF',
      '--vscode-button-secondaryBackground': '#E5E5E5', '--vscode-button-secondaryForeground': '#3B3B3B',
      '--vscode-panel-border': '#E5E5E5', '--vscode-input-background': '#FFFFFF', '--vscode-input-border': '#CECECE',
      '--vscode-input-foreground': '#3B3B3B', '--vscode-input-placeholderForeground': '#767676', '--vscode-textLink-foreground': '#005FB8',
      '--vscode-errorForeground': '#F85149', '--vscode-editorWarning-foreground': '#BF8803',
      '--vscode-charts-green': '#388A34', '--vscode-charts-yellow': '#BF8803', '--vscode-charts-red': '#E51400',
      '--vscode-charts-blue': '#0063D3', '--vscode-charts-orange': '#EA5C0055', '--vscode-charts-purple': '#652D90',
      '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent',
    },
  },
  hc: {
    label: 'Dark High Contrast', bodyClass: 'vscode-high-contrast',
    vars: {
      '--vscode-font-family': UI_FONT, '--vscode-editor-font-family': EDITOR_FONT,
      '--vscode-editor-background': '#000000', '--vscode-foreground': '#FFFFFF', '--vscode-descriptionForeground': '#FFFFFFB3',
      '--vscode-focusBorder': '#F38518', '--vscode-button-background': '#000000', '--vscode-button-foreground': '#FFFFFF',
      '--vscode-button-secondaryBackground': '#000000', '--vscode-button-secondaryForeground': '#FFFFFF',
      '--vscode-panel-border': '#6FC3DF', '--vscode-input-background': '#000000', '--vscode-input-border': '#6FC3DF',
      '--vscode-input-foreground': '#FFFFFF', '--vscode-input-placeholderForeground': '#FFFFFFB3', '--vscode-textLink-foreground': '#21A6FF',
      '--vscode-errorForeground': '#F48771', '--vscode-editorWarning-foreground': '#FFCC00',
      '--vscode-charts-green': '#89D185', '--vscode-charts-yellow': '#FFD370', '--vscode-charts-red': '#F48771',
      '--vscode-charts-blue': '#59A4F9', '--vscode-charts-purple': '#B180D7',
      '--vscode-contrastBorder': '#6FC3DF', '--vscode-contrastActiveBorder': '#F38518',
    },
  },
};

// The other themes VS Code 1.136 ships, for the contrast checks the three themes above cannot make on their own
// (coordinator, 27 September 2026: text on a card holds 4.5:1 in the stock themes too). Each is the theme file's
// own colours over the colour registry's defaults for what the file leaves out (textLink, the chart colours from
// editorError/Warning/Info and the find-match highlight), read from /usr/share/code/resources/app on 27 September
// 2026. themeId is the id VS Code puts on a webview's body as data-vscode-theme-id; tokens.css keys the smaller
// lifts on it. test/token-contrast.test.mjs checks every one of them; the screen check still renders only three.
const FONTS = { '--vscode-font-family': UI_FONT, '--vscode-editor-font-family': EDITOR_FONT };
export const STOCK_THEMES = {
  sollight: { label: 'Solarized Light', themeId: 'Solarized Light', bodyClass: 'vscode-light',
    vars: { ...FONTS, '--vscode-editor-background': '#FDF6E3', '--vscode-foreground': '#616161', '--vscode-descriptionForeground': '#717171', '--vscode-focusBorder': '#b49471', '--vscode-textLink-foreground': '#006AB1', '--vscode-errorForeground': '#A1260D', '--vscode-editorWarning-foreground': '#BF8803', '--vscode-charts-green': '#388A34', '--vscode-charts-yellow': '#BF8803', '--vscode-charts-red': '#E51400', '--vscode-charts-blue': '#0063D3', '--vscode-charts-purple': '#652D90', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#EA5C0055' } },
  soldark: { label: 'Solarized Dark', themeId: 'Solarized Dark', bodyClass: 'vscode-dark',
    vars: { ...FONTS, '--vscode-editor-background': '#002B36', '--vscode-foreground': '#CCCCCC', '--vscode-descriptionForeground': '#CCCCCCB3', '--vscode-focusBorder': '#2AA19899', '--vscode-textLink-foreground': '#3794FF', '--vscode-errorForeground': '#ffeaea', '--vscode-editorWarning-foreground': '#CCA700', '--vscode-charts-green': '#89D185', '--vscode-charts-yellow': '#CCA700', '--vscode-charts-red': '#F14C4C', '--vscode-charts-blue': '#59A4F9', '--vscode-charts-purple': '#B180D7', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#EA5C0055' } },
  quietlight: { label: 'Quiet Light', themeId: 'Quiet Light', bodyClass: 'vscode-light',
    vars: { ...FONTS, '--vscode-editor-background': '#F5F5F5', '--vscode-foreground': '#616161', '--vscode-descriptionForeground': '#717171', '--vscode-focusBorder': '#9769dc', '--vscode-textLink-foreground': '#006AB1', '--vscode-errorForeground': '#f1897f', '--vscode-editorWarning-foreground': '#BF8803', '--vscode-charts-green': '#388A34', '--vscode-charts-yellow': '#BF8803', '--vscode-charts-red': '#E51400', '--vscode-charts-blue': '#0063D3', '--vscode-charts-purple': '#652D90', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#edc9d899' } },
  light2026: { label: 'Light 2026', themeId: 'Light 2026', bodyClass: 'vscode-light',
    vars: { ...FONTS, '--vscode-editor-background': '#FFFFFF', '--vscode-foreground': '#202020', '--vscode-descriptionForeground': '#606060', '--vscode-focusBorder': '#0069CCFF', '--vscode-textLink-foreground': '#0069CC', '--vscode-errorForeground': '#ad0707', '--vscode-editorWarning-foreground': '#BF8803', '--vscode-charts-green': '#388A34', '--vscode-charts-yellow': '#667309', '--vscode-charts-red': '#ad0707', '--vscode-charts-blue': '#1A5CFF', '--vscode-charts-purple': '#652D90', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#d18616' } },
  monokai: { label: 'Monokai', themeId: 'Monokai', bodyClass: 'vscode-dark',
    vars: { ...FONTS, '--vscode-editor-background': '#272822', '--vscode-foreground': '#CCCCCC', '--vscode-descriptionForeground': '#CCCCCCB3', '--vscode-focusBorder': '#99947c', '--vscode-textLink-foreground': '#3794FF', '--vscode-errorForeground': '#F48771', '--vscode-editorWarning-foreground': '#CCA700', '--vscode-charts-green': '#89D185', '--vscode-charts-yellow': '#CCA700', '--vscode-charts-red': '#F14C4C', '--vscode-charts-blue': '#59A4F9', '--vscode-charts-purple': '#B180D7', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#EA5C0055' } },
  hclight: { label: 'High Contrast Light', themeId: 'Default High Contrast Light', bodyClass: ['vscode-high-contrast', 'vscode-high-contrast-light'],
    vars: { ...FONTS, '--vscode-editor-background': '#FFFFFF', '--vscode-foreground': '#292929', '--vscode-descriptionForeground': '#292929B3', '--vscode-focusBorder': '#006BBD', '--vscode-textLink-foreground': '#0F4A85', '--vscode-errorForeground': '#B5200D', '--vscode-editorWarning-foreground': '#895503', '--vscode-charts-green': '#374E06', '--vscode-charts-yellow': '#895503', '--vscode-charts-red': '#B5200D', '--vscode-charts-blue': '#0063D3', '--vscode-charts-purple': '#652D90', '--vscode-contrastBorder': '#0F4A85', '--vscode-contrastActiveBorder': '#006BBD' } },
  lightplus: { label: 'Light+', themeId: 'Light+', bodyClass: 'vscode-light',
    vars: { ...FONTS, '--vscode-editor-background': '#FFFFFF', '--vscode-foreground': '#616161', '--vscode-descriptionForeground': '#717171', '--vscode-focusBorder': '#0090F1', '--vscode-textLink-foreground': '#006AB1', '--vscode-errorForeground': '#A1260D', '--vscode-editorWarning-foreground': '#BF8803', '--vscode-charts-green': '#388A34', '--vscode-charts-yellow': '#BF8803', '--vscode-charts-red': '#E51400', '--vscode-charts-blue': '#0063D3', '--vscode-charts-purple': '#652D90', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#EA5C0055' } },
  darkplus: { label: 'Dark+', themeId: 'Dark+', bodyClass: 'vscode-dark',
    vars: { ...FONTS, '--vscode-editor-background': '#1E1E1E', '--vscode-foreground': '#CCCCCC', '--vscode-descriptionForeground': '#CCCCCCB3', '--vscode-focusBorder': '#007FD4', '--vscode-textLink-foreground': '#3794FF', '--vscode-errorForeground': '#F48771', '--vscode-editorWarning-foreground': '#CCA700', '--vscode-charts-green': '#89D185', '--vscode-charts-yellow': '#CCA700', '--vscode-charts-red': '#F14C4C', '--vscode-charts-blue': '#59A4F9', '--vscode-charts-purple': '#B180D7', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#EA5C0055' } },
  dark2026: { label: 'Dark 2026', themeId: 'Dark 2026', bodyClass: 'vscode-dark',
    vars: { ...FONTS, '--vscode-editor-background': '#121314', '--vscode-foreground': '#bfbfbf', '--vscode-descriptionForeground': '#8C8C8C', '--vscode-focusBorder': '#3994BCB3', '--vscode-textLink-foreground': '#48A0C7', '--vscode-errorForeground': '#f48771', '--vscode-editorWarning-foreground': '#CCA700', '--vscode-charts-green': '#86CF86', '--vscode-charts-yellow': '#E0B97F', '--vscode-charts-red': '#EF8773', '--vscode-charts-blue': '#57A3F8', '--vscode-charts-purple': '#AD80D7', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#CD861A' } },
  abyss: { label: 'Abyss', themeId: 'Abyss', bodyClass: 'vscode-dark',
    vars: { ...FONTS, '--vscode-editor-background': '#000c18', '--vscode-foreground': '#CCCCCC', '--vscode-descriptionForeground': '#CCCCCCB3', '--vscode-focusBorder': '#596F99', '--vscode-textLink-foreground': '#3794FF', '--vscode-errorForeground': '#F48771', '--vscode-editorWarning-foreground': '#CCA700', '--vscode-charts-green': '#89D185', '--vscode-charts-yellow': '#CCA700', '--vscode-charts-red': '#F14C4C', '--vscode-charts-blue': '#59A4F9', '--vscode-charts-purple': '#B180D7', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#eeeeee44' } },
  kimbie: { label: 'Kimbie Dark', themeId: 'Kimbie Dark', bodyClass: 'vscode-dark',
    vars: { ...FONTS, '--vscode-editor-background': '#221a0f', '--vscode-foreground': '#CCCCCC', '--vscode-descriptionForeground': '#CCCCCCB3', '--vscode-focusBorder': '#a57a4c', '--vscode-textLink-foreground': '#3794FF', '--vscode-errorForeground': '#F48771', '--vscode-editorWarning-foreground': '#CCA700', '--vscode-charts-green': '#89D185', '--vscode-charts-yellow': '#CCA700', '--vscode-charts-red': '#F14C4C', '--vscode-charts-blue': '#59A4F9', '--vscode-charts-purple': '#B180D7', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#EA5C0055' } },
  monodim: { label: 'Monokai Dimmed', themeId: 'Monokai Dimmed', bodyClass: 'vscode-dark',
    vars: { ...FONTS, '--vscode-editor-background': '#1e1e1e', '--vscode-foreground': '#CCCCCC', '--vscode-descriptionForeground': '#CCCCCCB3', '--vscode-focusBorder': '#3655b5', '--vscode-textLink-foreground': '#3794FF', '--vscode-errorForeground': '#F48771', '--vscode-editorWarning-foreground': '#CCA700', '--vscode-charts-green': '#89D185', '--vscode-charts-yellow': '#CCA700', '--vscode-charts-red': '#F14C4C', '--vscode-charts-blue': '#59A4F9', '--vscode-charts-purple': '#B180D7', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#EA5C0055' } },
  red: { label: 'Red', themeId: 'Red', bodyClass: 'vscode-dark',
    vars: { ...FONTS, '--vscode-editor-background': '#390000', '--vscode-foreground': '#CCCCCC', '--vscode-descriptionForeground': '#CCCCCCB3', '--vscode-focusBorder': '#ff6666aa', '--vscode-textLink-foreground': '#3794FF', '--vscode-errorForeground': '#ffeaea', '--vscode-editorWarning-foreground': '#CCA700', '--vscode-charts-green': '#89D185', '--vscode-charts-yellow': '#CCA700', '--vscode-charts-red': '#F14C4C', '--vscode-charts-blue': '#59A4F9', '--vscode-charts-purple': '#B180D7', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#EA5C0055' } },
  tnb: { label: 'Tomorrow Night Blue', themeId: 'Tomorrow Night Blue', bodyClass: 'vscode-dark',
    vars: { ...FONTS, '--vscode-editor-background': '#002451', '--vscode-foreground': '#CCCCCC', '--vscode-descriptionForeground': '#CCCCCCB3', '--vscode-focusBorder': '#bbdaff', '--vscode-textLink-foreground': '#3794FF', '--vscode-errorForeground': '#a92049', '--vscode-editorWarning-foreground': '#CCA700', '--vscode-charts-green': '#89D185', '--vscode-charts-yellow': '#CCA700', '--vscode-charts-red': '#F14C4C', '--vscode-charts-blue': '#59A4F9', '--vscode-charts-purple': '#B180D7', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#EA5C0055' } },
  vsdark: { label: 'Visual Studio Dark', themeId: 'Visual Studio Dark', bodyClass: 'vscode-dark',
    vars: { ...FONTS, '--vscode-editor-background': '#1E1E1E', '--vscode-foreground': '#CCCCCC', '--vscode-descriptionForeground': '#CCCCCCB3', '--vscode-focusBorder': '#007FD4', '--vscode-textLink-foreground': '#3794FF', '--vscode-errorForeground': '#F48771', '--vscode-editorWarning-foreground': '#CCA700', '--vscode-charts-green': '#89D185', '--vscode-charts-yellow': '#CCA700', '--vscode-charts-red': '#F14C4C', '--vscode-charts-blue': '#59A4F9', '--vscode-charts-purple': '#B180D7', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#EA5C0055' } },
  vslight: { label: 'Visual Studio Light', themeId: 'Visual Studio Light', bodyClass: 'vscode-light',
    vars: { ...FONTS, '--vscode-editor-background': '#FFFFFF', '--vscode-foreground': '#616161', '--vscode-descriptionForeground': '#717171', '--vscode-focusBorder': '#0090F1', '--vscode-textLink-foreground': '#006AB1', '--vscode-errorForeground': '#A1260D', '--vscode-editorWarning-foreground': '#BF8803', '--vscode-charts-green': '#388A34', '--vscode-charts-yellow': '#BF8803', '--vscode-charts-red': '#E51400', '--vscode-charts-blue': '#0063D3', '--vscode-charts-purple': '#652D90', '--vscode-contrastBorder': 'transparent', '--vscode-contrastActiveBorder': 'transparent', '--vscode-charts-orange': '#EA5C0055' } },
};
// The id VS Code gives the three main themes, so a page can be put in any theme the same way.
export const THEME_IDS = { dark: 'Dark Modern', light: 'Light Modern', hc: 'Default High Contrast' };

// Put a theme on the page the way VS Code does, or take it off again (id null). id is a key of THEMES or of
// STOCK_THEMES; the body gets the theme's class (both high-contrast classes for High Contrast Light, as VS Code
// does) and its data-vscode-theme-id.
export async function applyTheme(page, id) {
  const all = [...Object.values(THEMES), ...Object.values(STOCK_THEMES)];
  const t = id ? (THEMES[id] ?? STOCK_THEMES[id]) : null;
  if (id && !t) throw new Error(`no theme ${id}`);
  await page.evaluate((vars, cls, themeId, allVars, allClasses) => {
    const root = document.documentElement;
    for (const k of allVars) root.style.removeProperty(k);
    document.body.classList.remove(...allClasses);
    delete document.body.dataset.vscodeThemeId;
    if (vars) {
      for (const [k, v] of Object.entries(vars)) root.style.setProperty(k, v);
      document.body.classList.add(...cls);
      document.body.dataset.vscodeThemeId = themeId;
    }
  }, t ? t.vars : null, t ? [t.bodyClass].flat() : null, t ? (t.themeId ?? THEME_IDS[id]) : null,
  [...new Set(all.flatMap((x) => Object.keys(x.vars)))], [...new Set(all.flatMap((x) => [x.bodyClass].flat()))]);
}
