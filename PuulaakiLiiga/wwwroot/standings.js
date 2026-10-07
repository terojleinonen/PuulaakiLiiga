'use strict';
// League table rules, kept free of DOM/global state so they can be unit tested (tests/js).
// win = 3 points, draw = 1. Order: points, goal difference, goals for, team name.
function computeStandings({ teams = [], games = [], players = [], penalties = [] }) {
  const rows = new Map(teams.map(t => [t.id, { t, gp: 0, w: 0, d: 0, l: 0, gf: 0, ga: 0, pts: 0, pim: 0 }]));
  for (const g of games) {
    // Count only finished games between two different, existing teams.
    if (g.homeScore == null || g.awayScore == null || g.homeId === g.awayId) continue;
    const h = rows.get(g.homeId), a = rows.get(g.awayId);
    if (!h || !a) continue;
    h.gp++; a.gp++; h.gf += g.homeScore; h.ga += g.awayScore; a.gf += g.awayScore; a.ga += g.homeScore;
    if (g.homeScore > g.awayScore) { h.w++; a.l++; h.pts += 3; }
    else if (g.homeScore < g.awayScore) { a.w++; h.l++; a.pts += 3; }
    else { h.d++; a.d++; h.pts++; a.pts++; }
  }
  const teamOfPlayer = new Map(players.map(p => [p.id, p.teamId]));
  for (const p of penalties) { const r = rows.get(teamOfPlayer.get(p.playerId)); if (r) r.pim += p.minutes || 0; }
  return [...rows.values()].sort((a, b) =>
    b.pts - a.pts || (b.gf - b.ga) - (a.gf - a.ga) || b.gf - a.gf || a.t.name.localeCompare(b.t.name));
}
if (typeof module !== 'undefined') module.exports = { computeStandings };
