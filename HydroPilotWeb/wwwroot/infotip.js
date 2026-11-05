// Ayudas "i" (InfoTip): al abrirse (mouse o foco), el cartel elige hacia dónde abrirse para quedar dentro de la
// pantalla: arriba si abajo no entra (abajo estiraba la página y el menú lateral quedaba corto) y hacia el lado que
// entre. Delegación en el documento: sirve para los InfoTip que Blazor agrega después.
(function () {
    const margin = 12;

    function place(info) {
        const panel = info.querySelector('.hp-info__panel');
        if (!panel) return;
        info.classList.remove('hp-info--up', 'hp-info--flip');

        // Con :hover / :focus-within el cartel ya se ve: se mide donde quedó.
        const icon = info.getBoundingClientRect();
        const height = panel.offsetHeight;
        const below = window.innerHeight - icon.bottom - margin;
        const above = icon.top - margin;
        if (height > below && above > below) info.classList.add('hp-info--up');

        const box = panel.getBoundingClientRect();
        if (box.left < margin || box.right > window.innerWidth - margin) info.classList.add('hp-info--flip');
    }

    function open(event) {
        const info = event.target instanceof Element ? event.target.closest('.hp-info') : null;
        if (info && !info.contains(event.relatedTarget)) requestAnimationFrame(() => place(info));
    }

    document.addEventListener('mouseover', open);
    document.addEventListener('focusin', open);
})();
