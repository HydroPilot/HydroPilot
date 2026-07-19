window.theme = {
  init() {
    const saved = localStorage.getItem('theme');
    const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
    if (saved === 'dark' || (!saved && prefersDark)) {
      document.documentElement.dataset.theme = 'dark';
      document.documentElement.dataset.bsTheme = 'dark';
    }
  },
  toggle() {
    const current = document.documentElement.dataset.theme;
    const next = current === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.theme = next;
    document.documentElement.dataset.bsTheme = next;
    localStorage.setItem('theme', next);
  }
};
