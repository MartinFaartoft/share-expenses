// The running total under an exact split: "Assigned £45.00 of £50.00 · £5.00 left". Only a
// convenience: without it the form is checked when it is submitted. Amounts are read as the
// server reads them (digits, one . or ,, no more decimals than the currency has).
(() => {
  const toMinor = (text, places) => {
    const match = /^(\d+)(?:[.,](\d+))?$/.exec(text.trim());
    if (!match) return null;
    const fraction = match[2] ?? '';
    return fraction.length > places ? null : Number(match[1] + fraction.padEnd(places, '0'));
  };

  const update = (form) => {
    const line = form.querySelector('[data-exact-total]');
    if (!line || !form.elements['amount']) return;
    const places = Number(line.dataset.places);
    const show = (minor) => line.dataset.prefix + (minor / 10 ** places).toLocaleString('en-US', {
      minimumFractionDigits: places,
      maximumFractionDigits: places,
    });

    let assigned = 0;
    for (const box of form.querySelectorAll('input[name="participants"]:checked')) {
      const field = form.elements['amount-' + box.value];
      assigned += (field && toMinor(field.value, places)) || 0;
    }

    const total = toMinor(form.elements['amount'].value, places);
    if (total === null) line.textContent = 'Assigned ' + show(assigned);
    else if (total === assigned) line.textContent = 'Assigned ' + show(assigned) + ' of ' + show(total) + ' · adds up';
    else if (total > assigned) line.textContent = 'Assigned ' + show(assigned) + ' of ' + show(total) + ' · ' + show(total - assigned) + ' left';
    else line.textContent = 'Assigned ' + show(assigned) + ' of ' + show(total) + ' · ' + show(assigned - total) + ' over';
  };

  for (const type of ['input', 'change'])
    document.addEventListener(type, (event) => event.target.form && update(event.target.form));
  document.addEventListener('DOMContentLoaded', () => document.querySelectorAll('form').forEach(update));
})();
