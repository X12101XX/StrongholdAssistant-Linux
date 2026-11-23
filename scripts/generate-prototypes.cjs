// 从当前赛季的逐棋子备用配置生成替代关系，不按职业概括推测。
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const input = process.argv[2];
if (!input) throw Error('需要游戏数据目录（activity_table.json、character_table.json）');
const read = name => JSON.parse(fs.readFileSync(path.join(input, name + '.json'), 'utf8'));
const chars = read('character_table');
const season = read('activity_table').activity.AUTOCHESS.act3autochess.V3_1;
const names = new Set(JSON.parse(fs.readFileSync('data/operators.json', 'utf8')).operators.map(o => o.name));
const slots = Object.values(season.charShopChessDatas).filter(c => !c.isHidden && c.charId && names.has(chars[c.charId]?.name))
  .map(c => ({ name: chars[c.charId].name, tier: c.chessLevel, order: c.shopLevelSortId,
    profession: chars[c.charId].profession, branch: chars[c.charId].subProfessionId,
    prototype: c.backupCharId !== c.charId ? chars[c.backupCharId].name : null,
    prototypeId: c.backupCharId !== c.charId ? c.backupCharId : null,
    skillIndex: c.backupCharSkillIndex, bonds: season.charChessDataDict[c.chessId].bondIds }));
const prototypes = [...new Set(slots.map(c => c.prototypeId).filter(Boolean))].map(id => ({
  name: chars[id].name, charId: id, profession: chars[id].profession, branch: chars[id].subProfessionId,
  tiers: [...new Set(slots.filter(c => c.prototypeId === id).map(c => c.tier))].sort(),
  spine: `https://torappu.prts.wiki/assets/char_spine/${id}/meta.json`
}));
fs.writeFileSync('data/prototypes.json', JSON.stringify({ season: 'act3autochess/V3_1',
  source: 'https://github.com/Kengxxiao/ArknightsGameData/tree/master/zh_CN/gamedata/excel',
  hashes: Object.fromEntries(['activity_table', 'character_table'].map(n => [n,
    crypto.createHash('sha256').update(fs.readFileSync(path.join(input, n + '.json'))).digest('hex')])),
  prototypes, slots }, null, 2) + '\n');
console.log(`${prototypes.length} 个原型资源，${slots.filter(s => s.prototype).length} 条替代关系，${slots.length} 个排序位置`);
