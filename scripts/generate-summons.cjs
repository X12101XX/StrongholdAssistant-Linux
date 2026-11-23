// 当季棋子关联的召唤物；保留数据来源与动态阻挡说明，不把角色稀有度当作棋子阶数。
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const input = process.argv[2];
const read = n => JSON.parse(fs.readFileSync(path.join(input, n + '.json'), 'utf8'));
const chars = read('character_table');
const season = read('activity_table').activity.AUTOCHESS.act3autochess.V3_1;
const badges = { token_10003_cgbird_bird: 4, token_10018_robrta_mach: 3,
  token_10041_cathy_catsld: 3, token_10032_jesca2_jckshd: 4, token_10027_ironmn_pile3: 4 };
const summons = new Map(), unresolved = [];
for (const chess of Object.values(season.charShopChessDatas)) {
  for (const id of new Set([chess.charId, chess.backupCharId])) {
    const owner = chars[id];
    if (!owner) continue;
    const ids = new Set([...Object.keys(owner.displayTokenDict ?? {}),
      ...(owner.talents ?? []).flatMap(t => (t.candidates ?? []).map(c => c.tokenKey)).filter(Boolean),
      ...(owner.skills ?? []).map(s => s.overrideTokenKey).filter(Boolean)]);
    for (const tokenId of ids) {
      const token = chars[tokenId];
      if (!token) { if (!unresolved.some(t => t.tokenId === tokenId)) unresolved.push({ tokenId, owner: owner.name }); continue; }
      const stats = token.phases.at(-1).attributesKeyFrames.at(-1).data;
      const descriptions = [token.description, ...(token.talents ?? []).flatMap(t => (t.candidates ?? []).map(c => c.description))].filter(Boolean);
      const item = summons.get(tokenId) ?? {
        name: token.name, charId: tokenId, owners: [], ownerTiers: [],
        // 已核实的角标写证据；其他类型不根据角色稀有度猜测。
        badgeTier: badges[tokenId] ?? null,
        badgeEvidence: tokenId === 'token_10003_cgbird_bird' ? 'field-equipment-tier4.png / field-summon-side.png'
          : badges[tokenId] ? 'docs/research/truth/291/labels.json' : '待截图核实',
        occupiesCell: true, baseBlock: stats.blockCnt,
        dynamicBlock: descriptions.some(d => /阻挡数|阻挡.*复制|残骸形态|本格所有敌人/.test(d)),
        description: [...new Set(descriptions)].join('\n'),
        hp: stats.maxHp, defense: stats.def, resistance: stats.magicResistance,
        position: token.position,
        spine: `https://torappu.prts.wiki/assets/char_spine/${tokenId}/meta.json`
      };
      if (!item.owners.includes(owner.name)) item.owners.push(owner.name);
      if (!item.ownerTiers.includes(chess.chessLevel)) item.ownerTiers.push(chess.chessLevel);
      summons.set(tokenId, item);
    }
  }
}
const result = { season: 'act3autochess/V3_1',
  source: 'https://github.com/Kengxxiao/ArknightsGameData/tree/master/zh_CN/gamedata/excel',
  hashes: Object.fromEntries(['activity_table', 'character_table'].map(n => [n,
    crypto.createHash('sha256').update(fs.readFileSync(path.join(input, n + '.json'))).digest('hex')])),
  occupancyEvidence: '独立部署的 token 占据部署格；不等同于占用干员部署人数。角标未知项保留 null。',
  unresolved, summons: [...summons.values()] };
fs.writeFileSync('data/summons.json', JSON.stringify(result, null, 2) + '\n');
console.log(`${summons.size} 个召唤物资源，${unresolved.length} 个仅展示用引用未在角色表中定义`);

