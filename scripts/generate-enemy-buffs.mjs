// 来源：Kengxxiao/ArknightsGameData 的效果参数；路标档案馆的联机回合表
// 经 CarlosDJY 的 garrison_spawns.json 提供。只读取本地数据，不执行来源脚本。
// node scripts/generate-enemy-buffs.mjs <activity_table.json> <garrison_spawns.json> [--mock]
import fs from 'node:fs';

const [activityFile, spawnsFile] = process.argv.slice(2);
if (!activityFile || !spawnsFile) throw new Error('请指定 activity_table.json 和 garrison_spawns.json');
const read = file => JSON.parse(fs.readFileSync(file, 'utf8'));
const activity = read(activityFile).activity.AUTOCHESS.act3autochess.V3_1;
const schedules = read(spawnsFile).buffs;
const identity = [1, 1, 0, 1];
const equal = (a, b) => a.every((value, i) => Math.abs(value - b[i]) < 1e-8);

// 游戏表有效果参数，但没有各模式的发放时序。按参考表相邻回合之差匹配参数，
// 再用游戏表参数累计；数值相同的效果并不代表已经确定实际发放的效果 ID。
const effects = Object.entries(activity.effectBuffInfoDataDict)
  .filter(([id]) => id.startsWith('aceffect_enemy_'))
  .map(([id, buffs]) => {
    const values = [...identity];
    for (const buff of buffs) {
      const board = Object.fromEntries(buff.blackboard.map(p => [p.key, p.valueStr ?? p.value]));
      if (buff.key === 'enemy_attribute_mul' || buff.key === 'enemy_attribute_add') {
        for (const [key, value] of Object.entries(board)) {
          // TODO(design): 现有 EnemyBuff 接口不能表达逐效果的 enemy_exclude，详见 166 汇报。
          if (key === 'enemy_exclude') continue;
          if (buff.key === 'enemy_attribute_mul' && key === 'atk') values[0] *= value;
          else if (buff.key === 'enemy_attribute_mul' && key === 'max_hp') values[1] *= value;
          else if (buff.key === 'enemy_attribute_add' && key === 'attack_speed') values[2] += value;
          else throw new Error(`${id} 有接口尚未支持的属性：${buff.key}/${key}`);
        }
      } else if (buff.key === 'env_gbuff_new' && board.key === 'enemy_move_speed_mul') {
        values[3] *= 1 + board.move_speed;
      } else throw new Error(`${id} 有接口尚未支持的效果：${buff.key}`);
    }
    return values;
  });

const output = {};
for (const mode of ['mode_multi_funny', 'mode_multi_normal', 'mode_multi_hard', 'mode_multi_abyss']) {
  let previous = [...identity], total = [...identity];
  output[mode] = schedules[mode].map((row, index) => {
    if (row.length !== 4 || !row.every(Number.isFinite) || row[0] <= 0 || row[1] <= 0 || row[3] <= 0)
      throw new Error(`${mode} 第 ${index + 1} 回合数据无效`);
    const delta = row.map((value, i) => i === 2 ? value - previous[i] : value / previous[i]);
    const effect = equal(delta, identity) ? identity : effects.find(values => equal(values, delta));
    if (!effect) throw new Error(`${mode} 第 ${index + 1} 回合增量与游戏表不符：${delta}`);
    total = total.map((value, i) => i === 2 ? value + effect[i] : value * effect[i]);
    previous = row;
    const [atk, hp, attackSpeed, speed] = total;
    return { hp, atk, attackSpeed, speed };
  });
}

fs.writeFileSync('data/enemy_buffs.json', JSON.stringify(output, null, 2) + '\n');
if (process.argv.includes('--mock')) {
  const file = 'src/ui/src/lib/mock/catalog.json';
  const mock = read(file);
  mock.enemyBuffs = output;
  fs.writeFileSync(file, JSON.stringify(mock, null, 1) + '\n');
}
console.log(`输出 ${Object.keys(output).length} 个联机模式、${Object.values(output).reduce((n, rows) => n + rows.length, 0)} 个回合；全部增量与游戏表匹配。单人发放时序缺失，未生成。`);
