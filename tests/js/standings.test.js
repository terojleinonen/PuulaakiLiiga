'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { computeStandings } = require('../../PuulaakiLiiga/wwwroot/standings.js');

const team = (id, name) => ({ id, name });
const game = (homeId, awayId, homeScore, awayScore, id = Math.random()) => ({ id, homeId, awayId, homeScore, awayScore });
const table = data => computeStandings(data);
const names = rows => rows.map(r => r.t.name);
const A = team(1, 'Alpha'), B = team(2, 'Beta'), C = team(3, 'Gamma');

test('an empty league gives an empty table', () => {
  assert.deepEqual(table({}), []);
});

test('teams without games appear with zeros', () => {
  const [r] = table({ teams: [A] });
  assert.deepEqual({ ...r, t: undefined }, { t: undefined, gp: 0, w: 0, d: 0, l: 0, gf: 0, ga: 0, pts: 0, pim: 0 });
});

test('a win is worth 3 points, a loss 0; goals are recorded for both sides', () => {
  const [first, second] = table({ teams: [A, B], games: [game(1, 2, 3, 1)] });
  assert.equal(first.t.name, 'Alpha');
  assert.deepEqual([first.gp, first.w, first.d, first.l, first.gf, first.ga, first.pts], [1, 1, 0, 0, 3, 1, 3]);
  assert.deepEqual([second.gp, second.w, second.d, second.l, second.gf, second.ga, second.pts], [1, 0, 0, 1, 1, 3, 0]);
});

test('an away win counts for the visiting team', () => {
  const rows = table({ teams: [A, B], games: [game(1, 2, 0, 2)] });
  assert.equal(rows[0].t.name, 'Beta');
  assert.equal(rows[0].pts, 3);
  assert.equal(rows[1].pts, 0);
});

test('a draw is worth 1 point to each team', () => {
  const rows = table({ teams: [A, B], games: [game(1, 2, 2, 2)] });
  for (const r of rows) assert.deepEqual([r.d, r.w, r.l, r.pts], [1, 0, 0, 1]);
});

test('0–0 is a draw, not an unplayed game', () => {
  const rows = table({ teams: [A, B], games: [game(1, 2, 0, 0)] });
  assert.deepEqual(rows.map(r => [r.gp, r.pts]), [[1, 1], [1, 1]]);
});

test('unplayed games (no score) are ignored', () => {
  const rows = table({ teams: [A, B], games: [game(1, 2, null, null)] });
  assert.ok(rows.every(r => r.gp === 0 && r.pts === 0));
});

test('a game with only one score is ignored instead of producing NaN', () => {
  const rows = table({ teams: [A, B], games: [game(1, 2, 3, null), game(1, 2, null, 3)] });
  for (const r of rows) { assert.equal(r.gp, 0); assert.equal(r.gf, 0); assert.equal(r.ga, 0); }
});

test('games with a missing team or a team against itself are ignored', () => {
  const rows = table({ teams: [A, B], games: [game(1, 99, 5, 0), game(99, 2, 5, 0), game(1, 1, 4, 0)] });
  assert.ok(rows.every(r => r.gp === 0 && r.pts === 0 && r.gf === 0));
});

test('ordering: points first', () => {
  const rows = table({ teams: [A, B, C], games: [game(2, 1, 1, 0), game(3, 1, 5, 0), game(3, 2, 0, 1)] });
  assert.deepEqual(names(rows), ['Beta', 'Gamma', 'Alpha']);   // 6, 3, 0 points
});

test('ordering: equal points are split by goal difference, not goals for', () => {
  // Alpha beats Gamma 1–0, Beta beats Gamma 5–3: both 3 pts. GD Alpha +1, Beta +2 (but GF Beta 5 > 1).
  // Make GF disagree with GD: Alpha 10–9 (+1, GF 10), Beta 2–0 (+2, GF 2).
  const rows = table({ teams: [A, B, C], games: [game(1, 3, 10, 9), game(2, 3, 2, 0)] });
  assert.deepEqual(names(rows).slice(0, 2), ['Beta', 'Alpha']);
});

test('ordering: equal points and goal difference are split by goals for', () => {
  const D = team(4, 'Delta');
  // Alpha 3–2 (+1, GF 3), Beta 1–0 (+1, GF 1)
  const rows = table({ teams: [A, B, C, D], games: [game(1, 3, 3, 2), game(2, 4, 1, 0)] });
  assert.deepEqual(names(rows).slice(0, 2), ['Alpha', 'Beta']);
});

test('ordering: a full tie falls back to team name', () => {
  const rows = table({ teams: [C, B, A] });
  assert.deepEqual(names(rows), ['Alpha', 'Beta', 'Gamma']);
});

test('penalty minutes are added to the player’s team', () => {
  const players = [{ id: 10, teamId: 1 }, { id: 11, teamId: 1 }, { id: 12, teamId: 2 }];
  const penalties = [{ playerId: 10, minutes: 2 }, { playerId: 11, minutes: 5 }, { playerId: 12, minutes: 10 }];
  const byName = Object.fromEntries(table({ teams: [A, B], players, penalties }).map(r => [r.t.name, r.pim]));
  assert.deepEqual(byName, { Alpha: 7, Beta: 10 });
});

test('penalties without minutes, or for unknown players, add nothing', () => {
  const players = [{ id: 10, teamId: 1 }];
  const penalties = [{ playerId: 10, minutes: null }, { playerId: 10 }, { playerId: 404, minutes: 99 }];
  assert.equal(table({ teams: [A], players, penalties })[0].pim, 0);
});

test('penalties do not affect points or ordering', () => {
  const players = [{ id: 10, teamId: 2 }];
  const rows = table({ teams: [A, B], games: [game(1, 2, 1, 0)], players, penalties: [{ playerId: 10, minutes: 120 }] });
  assert.deepEqual(names(rows), ['Alpha', 'Beta']);
});

test('a small season adds up', () => {
  const rows = table({
    teams: [A, B, C],
    games: [game(1, 2, 3, 2), game(2, 3, 1, 1), game(3, 1, 0, 4), game(1, 2, null, null)],
  });
  const by = Object.fromEntries(rows.map(r => [r.t.name, r]));
  assert.deepEqual([by.Alpha.gp, by.Alpha.w, by.Alpha.pts, by.Alpha.gf, by.Alpha.ga], [2, 2, 6, 7, 2]);
  assert.deepEqual([by.Beta.gp, by.Beta.l, by.Beta.d, by.Beta.pts], [2, 1, 1, 1]);
  assert.deepEqual([by.Gamma.gp, by.Gamma.l, by.Gamma.d, by.Gamma.pts], [2, 1, 1, 1]);
  // total goals for == total goals against in any league
  assert.equal(rows.reduce((n, r) => n + r.gf, 0), rows.reduce((n, r) => n + r.ga, 0));
});

test('the input is not modified', () => {
  const data = { teams: [B, A], games: [game(1, 2, 1, 0, 7)], players: [], penalties: [] };
  const copy = structuredClone(data);
  table(data);
  assert.deepEqual(data, copy);
});
