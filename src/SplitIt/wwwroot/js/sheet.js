// Opens a sheet htmx has swapped in as a modal dialog (Escape, focus held, the page behind inert),
// and closes it on a tap on the backdrop. The sheet arrives open, so without this it is still a sheet.
document.addEventListener('htmx:afterSwap', (event) => {
  const dialog = event.detail.target.querySelector('dialog.sheet');
  if (!dialog) return;
  dialog.removeAttribute('open');
  dialog.showModal();
});

document.addEventListener('click', (event) => {
  if (event.target instanceof HTMLDialogElement && event.target.classList.contains('sheet')) event.target.close();
});
