import * as pdfjs from "./pdf.min.mjs";

const get = id => document.getElementById(id);
const viewport = get("viewport");
const canvas = get("canvas");
const status = get("status");
const chunks = [];
let pdf;
let pageNumber = 1;
let zoom = 1;
let renderRevision = 0;
let renderTask;
let searchRevision = 0;
let matches = [];
let matchIndex = 0;
const pageText = new Map();
const thumbnails = get("thumbnails");
const thumbnailQueue = [];
let drawingThumbnail = false;
const thumbnailObserver = new IntersectionObserver(entries => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    thumbnailObserver.unobserve(entry.target);
    thumbnailQueue.push(entry.target);
  }
  drawThumbnails();
}, { root: thumbnails.parentElement, rootMargin: "200px" });

function createThumbnails() {
  const fragment = document.createDocumentFragment();
  for (let number = 1; number <= pdf.numPages; number++) {
    const button = document.createElement("button");
    button.className = "thumbnail";
    button.dataset.page = number;
    button.setAttribute("aria-label", "Vai alla pagina " + number);
    const preview = document.createElement("canvas");
    preview.setAttribute("aria-hidden", "true");
    const label = document.createElement("span");
    label.textContent = number;
    button.append(preview, label);
    button.onclick = () => showPage(number);
    fragment.append(button);
  }
  thumbnails.replaceChildren(fragment);
  for (const button of thumbnails.children) thumbnailObserver.observe(button);
}

async function drawThumbnails() {
  if (drawingThumbnail) return;
  drawingThumbnail = true;
  try {
    while (thumbnailQueue.length) {
      const button = thumbnailQueue.shift();
      try {
        const page = await pdf.getPage(Number(button.dataset.page));
        const original = page.getViewport({ scale: 1 });
        const view = page.getViewport({ scale: 112 / original.width });
        const preview = button.querySelector("canvas");
        preview.width = Math.ceil(view.width * 1.5);
        preview.height = Math.ceil(view.height * 1.5);
        preview.style.aspectRatio = original.width + " / " + original.height;
        await page.render({ canvas: preview, viewport: view, transform: [1.5, 0, 0, 1.5, 0, 0], annotationMode: pdfjs.AnnotationMode.DISABLE }).promise;
        preview.dataset.ready = "";
      } catch (error) {
        console.error(error);
        button.title = "Anteprima non disponibile. Premi per aprire la pagina.";
      }
    }
  } finally {
    drawingThumbnail = false;
  }
}

pdfjs.GlobalWorkerOptions.workerSrc = new URL("./pdf.worker.min.mjs", import.meta.url).href;
const asset = path => new URL(path, import.meta.url).href;

function fail(error) {
  console.error(error);
  status.hidden = false;
  status.textContent = "Impossibile aprire o visualizzare il documento.";
}

window.addEventListener("HybridWebViewMessageReceived", async event => {
  try {
    const message = event.detail.message;
    if (message !== "end") {
      chunks.push(Uint8Array.from(atob(message), character => character.charCodeAt(0)));
      window.HybridWebView.SendRawMessage("next");
      return;
    }
    const pdfBytes = new Uint8Array(chunks.reduce((total, chunk) => total + chunk.length, 0));
    let offset = 0;
    for (const chunk of chunks) {
      pdfBytes.set(chunk, offset);
      offset += chunk.length;
      chunk.fill(0);
    }
    chunks.length = 0;

    pdf = await pdfjs.getDocument({
      data: pdfBytes,
      cMapUrl: asset("./cmaps/"),
      cMapPacked: true,
      standardFontDataUrl: asset("./standard_fonts/"),
      wasmUrl: asset("./wasm/"),
      iccUrl: asset("./iccs/"),
      isEvalSupported: false,
      enableXfa: false
    }).promise;
    get("find").disabled = false;
    createThumbnails();
    await render();
  } catch (error) {
    fail(error);
  }
});

function updateNavigation() {
  get("page").textContent = pageNumber + " / " + pdf.numPages;
  get("previous").disabled = pageNumber <= 1;
  get("next").disabled = pageNumber >= pdf.numPages;
  get("zoom-out").disabled = zoom <= 0.7;
  get("zoom-in").disabled = zoom >= 2.5;
  const selected = thumbnails.querySelector('[aria-current="page"]');
  const active = thumbnails.children[pageNumber - 1];
  if (selected !== active) {
    selected?.removeAttribute("aria-current");
    active?.setAttribute("aria-current", "page");
    active?.scrollIntoView({ block: "nearest" });
  }
}

async function render() {
  if (!pdf) return;
  const revision = ++renderRevision;
  if (renderTask) {
    renderTask.cancel();
    await renderTask.promise.catch(() => {});
  }
  const current = await pdf.getPage(pageNumber);
  if (revision !== renderRevision) return;
  const original = current.getViewport({ scale: 1 });
  const view = current.getViewport({ scale: Math.max(1, viewport.clientWidth - 24) / original.width * zoom });
  const output = Math.min(window.devicePixelRatio || 1, 2, 4096 / Math.max(view.width, view.height));
  canvas.width = Math.ceil(view.width * output);
  canvas.height = Math.ceil(view.height * output);
  canvas.style.width = view.width + "px";
  canvas.style.height = view.height + "px";
  updateNavigation();
  renderTask = current.render({
    canvas,
    viewport: view,
    transform: [output, 0, 0, output, 0, 0],
    annotationMode: pdfjs.AnnotationMode.DISABLE
  });
  try {
    await renderTask.promise;
  } catch (error) {
    if (error.name !== "RenderingCancelledException") throw error;
    return;
  }
  if (revision === renderRevision) {
    canvas.dataset.ready = "";
    status.hidden = true;
  }
}

function showPage(number) {
  pageNumber = Math.max(1, Math.min(number, pdf.numPages));
  viewport.scrollTo(0, 0);
  render().catch(fail);
}

function normalize(value) {
  return value.normalize("NFKC").toLocaleLowerCase("it").replace(/\s+/gu, " ").trim();
}

async function textOf(number) {
  if (!pageText.has(number)) {
    const page = await pdf.getPage(number);
    const content = await page.getTextContent();
    let text = "";
    for (const item of content.items) {
      if ("str" in item) text += item.str + (item.hasEOL ? " " : "");
    }
    pageText.set(number, normalize(text));
  }
  return pageText.get(number);
}

function selectMatch(index) {
  matchIndex = index;
  const number = matches[index];
  get("count").textContent = "Pagina " + number + " · " + (index + 1) + " di " + matches.length;
  get("match-previous").disabled = index === 0;
  get("match-next").disabled = index === matches.length - 1;
  showPage(number);
}

get("search").addEventListener("submit", async event => {
  event.preventDefault();
  if (!pdf) return;
  const query = normalize(get("query").value);
  const revision = ++searchRevision;
  get("matches").hidden = !query;
  if (!query) return;
  get("count").textContent = "Ricerca in corso…";
  get("match-previous").disabled = true;
  get("match-next").disabled = true;
  try {
    const results = [];
    for (let number = 1; number <= pdf.numPages; number++) {
      const text = await textOf(number);
      if (revision !== searchRevision) return;
      if (text.includes(query)) results.push(number);
    }
    matches = results;
    if (matches.length) selectMatch(0);
    else get("count").textContent = "Nessuna pagina trovata.";
  } catch (error) {
    get("count").textContent = "Ricerca non disponibile per questo documento.";
    console.error(error);
  }
});

get("previous").onclick = () => showPage(pageNumber - 1);
get("next").onclick = () => showPage(pageNumber + 1);
get("zoom-out").onclick = () => { zoom = Math.max(0.7, zoom - 0.2); render().catch(fail); };
get("zoom-in").onclick = () => { zoom = Math.min(2.5, zoom + 0.2); render().catch(fail); };
get("match-previous").onclick = () => selectMatch(matchIndex - 1);
get("match-next").onclick = () => selectMatch(matchIndex + 1);
window.addEventListener("resize", () => render().catch(fail));
window.addEventListener("contextmenu", event => event.preventDefault());
window.addEventListener("dragstart", event => event.preventDefault());
window.addEventListener("keydown", event => {
  if ((event.ctrlKey || event.metaKey) && ["p", "s", "u", "c"].includes(event.key.toLowerCase()) ||
      event.key === "F12")
    event.preventDefault();
});
if (window.HybridWebView) window.HybridWebView.SendRawMessage("next");
else fail(new Error("Bridge locale non disponibile"));
