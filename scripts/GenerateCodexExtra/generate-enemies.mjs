import fs from 'node:fs';
import path from 'node:path';

// 从仓库根目录运行；可传入本地国服 gamedata 目录，不下载数据表。
const source = process.argv[2] ?? 'docs/research/refs/ArknightsGameData/zh_CN/gamedata';
const levelDirectory = process.argv[3] ?? 'docs/research/refs/autochess-levels';
const activityFile = process.argv[4] ?? 'docs/research/refs/activity_table.json';
const read = file => JSON.parse(fs.readFileSync(file, 'utf8'));
const handbook = read(path.join(source, 'excel/enemy_handbook_table.json')).enemyData;
const database = new Map(read(path.join(source, 'levels/enemydata/enemy_database.json')).enemies
  .map(e => [e.Key, e.Value.find(v => v.level === 0)?.enemyData]));
const targets = new Map();
for (const type of Object.values(read('data/enemy_types.json').types))
  for (const enemy of [...type.firstHalfVariants, ...type.secondHalfVariants])
    targets.set(enemy.specialEnemyId, { name: enemy.enemyName, level: 'NORMAL' });
for (const boss of read('data/bosses.json').bosses)
  targets.set(boss.enemyId, { name: boss.name, level: 'BOSS' });

// 路线表引用的关卡还包含普通敌人；数据库引用覆盖波次、分支与召唤使用的敌人。
const levels = new Set(Object.values(read('data/garrison_routes.json').battles)
  .flatMap(rounds => Object.values(rounds).flat()).map(entry => entry.level));
const missingLevels = [];
for (const level of levels) {
  const file = path.join(levelDirectory, `${level.startsWith('level_') ? level : `level_act3autochess_${level}`}.json`);
  if (!fs.existsSync(file)) { missingLevels.push(level); continue; }
  for (const enemy of read(file).enemyDbRefs) {
    if (targets.has(enemy.id)) continue;
    targets.set(enemy.id, { name: database.get(enemy.id)?.name.m_value ?? enemy.id, level: 'NORMAL' });
  }
}
if (missingLevels.length) console.warn(`缺关卡：${missingLevels.join('、')}`);
// 特训敌人会携带普通敌人（例如宿主士兵），这些运行时追加项不在关卡 enemyDbRefs 内。
const activity = read(activityFile).activity.AUTOCHESS.act3autochess.V3_1;
const clean = text => text.replace(/<[@$][^>]*>|<\/>/g, '').replace(/\\n/g, '\n');
const bountyKinds = { add_enemy_kill_gain_coin: 'kill', add_enemy_selfbattle_win_gain_coin: 'perfect' };
const bounties = Object.keys(activity.effectInfoDataDict).sort()
  .filter(id => activity.effectInfoDataDict[id].effectType === 'ENEMY_GAIN').map(id => {
    const effect = activity.effectInfoDataDict[id];
    const buff = activity.effectBuffInfoDataDict[id].find(b => b.key in bountyKinds);
    if (!buff) throw new Error(`悬赏缺少奖励效果：${id}`);
    const values = Object.fromEntries(buff.blackboard.map(b => [b.key, b.valueStr ?? b.value]));
    if (![1, 2, 99].includes(values.round)) throw new Error(`未知悬赏持续场数：${id} ${values.round}`);
    return {
      id, name: effect.effectName, kind: bountyKinds[buff.key], enemyId: values.enemy_id,
      enemyName: handbook[values.enemy_id]?.name ?? database.get(values.enemy_id)?.name.m_value ?? values.enemy_id,
      count: values.count, coin: values.coin, rounds: values.round === 99 ? null : values.round,
      tag: effect.effectName.match(/飞行|频次|损伤|持续|隐匿|折射|特异/)?.[0] ?? null,
      grade: effect.effectName.match(/I{1,3}$/)?.[0].length ?? null,
      text: clean(effect.effectDesc),
    };
  });
const extraEnemies = [...Object.values(activity.enemyInfoDict).flat(),
  ...Object.values(activity.specialEnemyInfoDict).flatMap(e => e.attachedNormalEnemyKeys ?? []),
  ...bounties.map(b => b.enemyId)];
for (const id of extraEnemies)
  if (!targets.has(id)) targets.set(id, { name: database.get(id)?.name.m_value ?? id, level: 'NORMAL' });

const damageNames = { PHYSIC: '物理', MAGIC: '法术', HEAL: '治疗', NO_DAMAGE: '无' };
const missingHandbook = [], missingDatabase = [];
const enemies = [...targets.keys()].sort().map(id => {
  const info = handbook[id], base = database.get(id), fallback = targets.get(id);
  if (!info) missingHandbook.push(id);
  if (!base) missingDatabase.push(id);
  // 第 0 级没有前级可继承；未定义字段使用表内序列化默认值，缺属性表时数值为 0。
  const number = key => base ? base.attributes[key].m_value : 0;
  return {
    id, name: info?.name ?? fallback.name, level: info?.enemyLevel ?? fallback.level,
    attackType: info?.attackType ?? '',
    damage: (info?.damageType ?? []).map(type => {
      if (!(type in damageNames)) throw new Error(`未知伤害类型：${id} ${type}`);
      return damageNames[type];
    }),
    motion: base?.motion.m_value ?? 'WALK',
    hp: number('maxHp'), atk: number('atk'), def: number('def'), res: number('magicResistance'),
    attackTime: number('baseAttackTime'), speed: number('moveSpeed'), weight: number('massLevel'),
    abilities: (info?.abilityList ?? []).map(a => clean(a.text)),
    description: clean(info?.description ?? ''),
  };
});
fs.writeFileSync('data/enemies.json', JSON.stringify(enemies, null, 2) + '\n');
fs.writeFileSync('data/bounties.json', JSON.stringify(bounties, null, 2) + '\n');
// 界面假数据由设计负责；需要同步时显式传入 --mock。
if (process.argv.includes('--mock')) {
  const mockPath = 'src/ui/src/lib/mock/catalog.json';
  const mock = read(mockPath);
  mock.enemies = enemies;
  mock.bounties = bounties;
  fs.writeFileSync(mockPath, JSON.stringify(mock, null, 1) + '\n');
}
console.log(`输出：${enemies.length} 个敌人；缺图鉴：${missingHandbook.join('、') || '无'}；缺属性：${missingDatabase.join('、') || '无'}`);
console.log(`悬赏：${bounties.length} 条，涉及 ${new Set(bounties.map(b => b.enemyId)).size} 种敌人`);
