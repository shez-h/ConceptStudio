const $ = id => document.getElementById(id);
let session = null, selectedRevision = null, busy = false, configured = false;
const reviewErrors = new Map();
const emptyConcepts = $('concepts').innerHTML;
let dirty = false;
function markDirty() { dirty = true; $('save-state').textContent = 'Unsaved changes'; }
function saveStatus() {
  $('save-state').textContent = dirty ? 'Unsaved changes' : session?.savedAt ? `Saved ${new Date(session.savedAt).toLocaleString()}` : 'Not saved yet';
}
async function ensureSession() {
  if (!session) {
    session = await api('/api/sessions', { brief: $('brief').value });
    localStorage.setItem('concept-studio-session', session.id);
  }
}
async function refreshSavedSessions() {
  const saved = await api('/api/sessions');
  $('saved-sessions').replaceChildren(node('option', 'Choose a saved session…'));
  $('saved-sessions').firstChild.value = '';
  for (const item of saved) { const option = node('option', `${item.name} · ${item.conceptCount} concepts`); option.value = item.id; $('saved-sessions').append(option); }
}
function displaySession(next) {
  session = next; selectedRevision = null; reviewErrors.clear(); dirty = false;
  $('session-name').value = session.name; $('brief').value = session.brief;
  $('feedback').value = ''; $('preview-panel').hidden = true;
  localStorage.setItem('concept-studio-session', session.id); renderConcepts();
  session.revisions.filter(r => !r.review).forEach(r => reviewErrors.set(r.id, 'No completed review. You can request one below.'));
  const revision = session.revisions.find(r => r.id === session.selectedRevisionId) || session.revisions.at(-1);
  if (revision) showRevision(revision);
  dirty = !!session.hasUnsavedChanges; saveStatus();
}
function node(tag, text, cls) { const el = document.createElement(tag); if (text != null) el.textContent = text; if (cls) el.className = cls; return el; }
async function api(path, body) {
  const response = await fetch(path, body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || `Request failed (${response.status}). Please try again.`);
  return data;
}
function lock(value, status) {
  busy = value; document.querySelectorAll('button, textarea, input, select').forEach(el => el.disabled = value);
  $('explore').disabled = value || !configured;
  $('activity-text').textContent = status;
  $('concept-stage').classList.toggle('working', value);
  $('concept-stage').setAttribute('aria-busy', String(value));
  $('concept-content').inert = value;
  $('concept-loading').hidden = !value;
  $('concept-loading-text').textContent = status;
}
function error(message) { $('error').textContent = message; $('error').hidden = !message; }
function showReview(revision) {
  const list = $('review'); list.replaceChildren(); $('retry-review').hidden = true;
  if (!revision.review) {
    list.append(node('p', reviewErrors.get(revision.id) || 'Review pending…', 'fine'));
    $('retry-review').hidden = !reviewErrors.has(revision.id); return;
  }
  if (!revision.review.findings.length) { list.append(node('p', 'No applied guidelines to assess.', 'fine')); return; }
  for (const finding of revision.review.findings) {
    const item = node('div', null, 'finding');
    item.append(node('span', finding.verdict, `verdict ${finding.verdict}`));
    item.append(node('p', session.concepts[revision.conceptIndex].appliedGuidelines[finding.id - 1], 'requirement'));
    item.append(node('p', finding.explanation, 'fine')); list.append(item);
  }
}
function showRevision(revision) {
  if (selectedRevision?.id !== revision.id) markDirty();
  selectedRevision = revision; $('preview-panel').hidden = false;
  $('preview-title').textContent = session.concepts[revision.conceptIndex].title;
  $('preview').src = revision.imageUrl; $('open-image').href = revision.imageUrl;
  $('preview').alt = `${session.concepts[revision.conceptIndex].title} — ${revision.feedback || 'initial visualization'}`;
  $('revisions').replaceChildren();
  session.revisions.filter(r => r.conceptIndex === revision.conceptIndex).forEach((r, i) => {
    const button = node('button', i === 0 ? 'Original' : `Revision ${i}`, 'revision');
    button.title = r.feedback || 'Initial visualization'; button.setAttribute('aria-pressed', String(r.id === revision.id));
    button.disabled = busy; button.onclick = () => showRevision(r); $('revisions').append(button);
  });
  showReview(revision);
}
function renderConcepts() {
  $('concepts').replaceChildren(); $('concept-count').textContent = `${session.concepts.length} concepts`;
  if (!session.concepts.length) $('concepts').innerHTML = emptyConcepts;
  session.concepts.forEach((concept, index) => {
    const card = node('article', null, 'concept');
    card.append(node('span', `CONCEPT ${String(index + 1).padStart(2, '0')}`, 'eyebrow'));
    card.append(node('h3', concept.title), node('p', concept.description));
    const briefDetails = node('details', null, 'concept-brief');
    briefDetails.append(node('summary', 'Original brief'));
    briefDetails.append(node('p', concept.brief ?? 'The original brief was not recorded for this older concept.', 'fine'));
    card.append(briefDetails);
    const details = node('details'); details.append(node('summary', 'Prompt & applied guidelines'));
    details.append(node('p', concept.imagePrompt, 'fine'));
    const ul = node('ul'); concept.appliedGuidelines.forEach(g => ul.append(node('li', g))); details.append(ul); card.append(details);
    const button = node('button', 'Visualize this concept ↗', 'secondary'); button.onclick = () => generateImage(index); card.append(button);
    const revisions = session.revisions.filter(r => r.conceptIndex === index);
    if (revisions.length) {
      const view = node('button', `View ${revisions.length} preview${revisions.length === 1 ? '' : 's'}`, 'text-button');
      view.onclick = () => { showRevision(revisions.at(-1)); $('preview-panel').scrollIntoView({ behavior: 'smooth' }); };
      card.append(view);
    }
    const remove = node('button', 'Delete concept', 'text-button delete-concept');
    remove.type = 'button'; remove.disabled = busy;
    remove.setAttribute('aria-label', `Delete concept: ${concept.title}`);
    remove.onclick = () => deleteConcept(index); card.append(remove);
    $('concepts').append(card);
  });
  $('trace-wrap').hidden = !session.trace.length; $('trace').textContent = session.trace.join('\n');
}
async function deleteConcept(index) {
  if (busy || !session) return;
  const concept = session.concepts[index];
  if (!confirm(`Delete “${concept.title}” and its image revisions and reviews from this session? Save the session afterwards to keep this change.`)) return;
  error(''); lock(true, 'Removing concept…');
  try {
    const selectedId = selectedRevision?.id;
    session = await api(`/api/sessions/${session.id}/concepts/${index}/delete`, {});
    for (const id of reviewErrors.keys()) {
      if (!session.revisions.some(r => r.id === id)) reviewErrors.delete(id);
    }
    renderConcepts();
    selectedRevision = session.revisions.find(r => r.id === selectedId) || null;
    if (selectedRevision) showRevision(selectedRevision);
    else {
      $('preview-panel').hidden = true;
      $('preview').removeAttribute('src'); $('open-image').removeAttribute('href');
      $('feedback').value = ''; $('revisions').replaceChildren(); $('review').replaceChildren();
    }
    markDirty();
  } catch (ex) { error(`Could not delete concept: ${ex.message}`); }
  finally { lock(false, 'Ready when you are.'); }
}
async function review(revision) {
  try {
    revision.review = await api(`/api/sessions/${session.id}/images/${revision.id}/review`, {});
    reviewErrors.delete(revision.id); markDirty();
  } catch (ex) { reviewErrors.set(revision.id, `Review unavailable: ${ex.message} Your image is ready and can still be edited.`); }
  if (selectedRevision?.id === revision.id) showReview(revision);
}
async function generateImage(conceptIndex, sourceRevisionId = null, feedback = null) {
  if (busy) return; error(''); lock(true, sourceRevisionId ? 'Refining your image… this can take a few minutes.' : 'Bringing your concept to life… this can take a few minutes.');
  try {
    const revision = await api(`/api/sessions/${session.id}/images`, { conceptIndex, sourceRevisionId, feedback });
    session.revisions.push(revision); renderConcepts(); showRevision(revision); markDirty(); $('feedback').value = '';
    $('preview-panel').scrollIntoView({ behavior: 'smooth', block: 'start' });
    lock(true, 'Image ready. Taking a second look at the guidelines…'); await review(revision);
  } catch (ex) { error(ex.message); }
  finally { lock(false, 'Ready for your next idea.'); }
}
$('brief-form').onsubmit = async event => {
  event.preventDefault(); if (busy) return; error(''); lock(true, 'Exploring your brief with the studio tools…');
  try {
    await ensureSession();
    session = await api(`/api/sessions/${session.id}/concepts`, { brief: $('brief').value, count: Number($('generation-count').value) });
    renderConcepts(); markDirty();
  } catch (ex) { error(ex.message); }
  finally { lock(false, 'Choose a concept to visualize.'); }
};
$('edit-form').onsubmit = event => { event.preventDefault(); if (selectedRevision) generateImage(selectedRevision.conceptIndex, selectedRevision.id, $('feedback').value); };
$('retry-review').onclick = async () => {
  if (busy || !selectedRevision) return;
  lock(true, 'Reviewing the selected image…'); try { await review(selectedRevision); } finally { lock(false, 'Review finished.'); }
};
$('sample').onclick = () => { $('brief').value = 'An underwater exploration vehicle using the studio sunset palette. Follow studio guidelines, with a playful silhouette.'; $('brief').focus(); markDirty(); };
async function start() {
  lock(true, 'Connecting to your studio…');
  try {
    const config = await api('/api/config'); configured = config.configured; $('setup').hidden = configured;
    $('model-label').textContent = config.imageModel;
    await refreshSavedSessions();
    const id = localStorage.getItem('concept-studio-session');
    if (id) {
      try { displaySession(await api(`/api/sessions/${id}`)); }
      catch { localStorage.removeItem('concept-studio-session'); }
    }
  } catch (ex) { error(ex.message); }
  finally { lock(false, configured ? 'Ready when you are.' : 'Waiting for server API configuration.'); }
}
$('generation-count').addEventListener('change', () => {
  $('explore').textContent = Number($('generation-count').value) === 1 ? 'Add concept →' : 'Add concepts →';
});
$('brief').addEventListener('input', markDirty);
$('session-name').addEventListener('input', markDirty);
$('save-session').onclick = async () => {
  if (busy) return;
  if (!$('session-name').value.trim()) { error('Give your design session a name.'); $('session-name').focus(); return; }
  error(''); lock(true, 'Saving your design session…');
  try {
    await ensureSession();
    const saved = await api(`/api/sessions/${session.id}/save`, {
      name: $('session-name').value, brief: $('brief').value, selectedRevisionId: selectedRevision?.id || null
    });
    session.savedAt = saved.savedAt; session.name = saved.name; session.brief = saved.brief;
    dirty = false; saveStatus(); await refreshSavedSessions();
  } catch (ex) { error(`Could not save: ${ex.message}`); }
  finally { lock(false, 'Ready when you are.'); }
};
$('new-session').onclick = async () => {
  if (busy || (dirty && !confirm('Start a new session? Changes since your last save will not be saved.'))) return;
  error(''); lock(true, 'Creating a new design session…');
  try { displaySession(await api('/api/sessions', { brief: '' })); }
  catch (ex) { error(ex.message); }
  finally { lock(false, 'Describe your next idea.'); }
};
$('open-session').onclick = async () => {
  const id = $('saved-sessions').value;
  if (busy || !id || (dirty && !confirm('Open the saved session? Changes since your last save will not be saved.'))) return;
  error(''); lock(true, 'Opening your saved design…');
  try { displaySession(await api(`/api/sessions/${id}/open`, {})); }
  catch (ex) { error(`Could not open: ${ex.message}`); }
  finally { lock(false, 'Ready to pick up where you left off.'); }
};
window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
start();
