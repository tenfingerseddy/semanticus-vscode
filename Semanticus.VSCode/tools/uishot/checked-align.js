// Adapted from proto/checked c0fff98a: measure the card, fixed History column and Query glyph column.
() => {
  const faults = [];
  const box = (e) => e.getBoundingClientRect();
  const visible = (e) => !!e.getClientRects().length && getComputedStyle(e).visibility !== 'hidden';
  for (const card of [...document.querySelectorAll('.ck-card')].filter(visible)) {
    const host = card.closest('.ck-pop'), r = box(card), h = box(host);
    for (const child of [...card.children].filter(visible)) {
      const b = box(child);
      if (Math.abs(b.left - r.left) > 0.5 || Math.abs(b.right - r.right) > 0.5) faults.push('Card edges: ' + child.className);
    }
    for (const row of card.querySelectorAll('.ck-row')) {
      if (Math.abs(box(row.firstElementChild).left - r.left) > 0.5) faults.push('Row label: ' + row.textContent);
      if (Math.abs(box(row.lastElementChild).right - r.right) > 0.5) faults.push('Row value: ' + row.textContent);
    }
    if (h.left < 0 || h.right > innerWidth + 0.5 || h.top < 0 || h.bottom > innerHeight + 0.5) faults.push('Card outside window');
    for (const leaf of [...card.querySelectorAll('*')].filter((e) => !e.children.length && visible(e))) {
      if (leaf.scrollWidth > leaf.clientWidth + 1 && getComputedStyle(leaf).overflow !== 'visible') faults.push('Clipped words: ' + leaf.textContent);
    }
    const speed = card.querySelector('.ck-speed > span:last-child');
    if (speed) {
      const walker = document.createTreeWalker(speed, NodeFilter.SHOW_TEXT);
      const words = [];
      while (walker.nextNode()) {
        const node = walker.currentNode;
        for (const match of node.textContent.matchAll(/\S+/g)) {
          const range = document.createRange();
          range.setStart(node, match.index); range.setEnd(node, match.index + match[0].length);
          words.push({ word: match[0], top: box(range).top });
        }
      }
      const clock = words.findIndex((w) => w.word === 'time).');
      if (clock > 0 && Math.abs(words[clock].top - words[clock - 1].top) > 0.5) faults.push('Clock words split in Speed');
    }
  }
  const titles = [...document.querySelectorAll('.ph-row .ph-title')].filter(visible).map((e) => ({
    id: e.closest('.ph-row').dataset.hid, text: e.textContent, width: box(e).width,
    whatWidth: box(e.closest('.ph-what')).width,
  }));
  for (const title of titles) if (title.width < 100) faults.push('History title too narrow: ' + title.id + ' ' + title.width);
  const historyText = [...document.querySelectorAll('.ph-row')].flatMap((row) =>
    [...row.querySelectorAll('.ph-time, .ph-who, .ph-what, .ph-title, .ph-meta, .ph-kidtitle, .ph-ver, .ph-act, .ph-ver .ck-mark, .ph-ver .ck-mark > span:last-child, .ph-act button, .ph-later')]
      .filter((e) => visible(e) && e.textContent.trim()).map((e) => ({
        id: row.dataset.hid, cell: e.className || e.tagName, text: e.textContent,
        clientWidth: e.clientWidth, scrollWidth: e.scrollWidth,
      })));
  for (const cell of historyText) {
    if (cell.scrollWidth > cell.clientWidth) faults.push('History text clipped: ' + cell.id + ' ' + cell.cell + ' ' + cell.clientWidth + '/' + cell.scrollWidth);
  }
  const header = document.querySelector('#root .hdr'), pop = document.querySelector('.ck-pop');
  if (header && pop && box(pop).top < box(header).bottom) faults.push('Properties card covers its header');
  for (const selector of ['.ph-row .ph-ver .ck-mark', '.qn-line .qn-row-acts .ck-mark']) {
    const starts = [...document.querySelectorAll(selector)].filter(visible).map((e) => box(e).left);
    if (starts.length > 1 && Math.max(...starts) - Math.min(...starts) > 0.5) faults.push('Mark column: ' + selector);
  }
  return { faults: faults.length, details: faults, titles, historyText, sideways: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1 };
}
