// 广场假数据：浏览器里开发（没有宿主）且地址带 ?demo 时，广场接口不连线上，用这几条示例阵容，
// 方便看排版、试导入（也不会把线上用户的内容拍进截图）。照广场网站的 src/plaza/site/src/demo.ts 做
import type { Post } from '../plaza.svelte'
import raw from './catalog.json'

type Op = { name: string; tier: number; obtain: string | null; core: string[]; extra: string[]; skills?: { id: string }[] }
const ops = raw.operators as unknown as Op[]
const eqs = raw.equipment.map((e) => e.name)
const strategies = raw.strategies.map((s) => s.id)
const cov = (name: string) => raw.covenants.find((c) => c.name === name)!.id

function build(i: number, name: string, goals: { id: string; level: 1 | 2 }[], description: string): Post {
  const members = ops
    .filter((o) => !o.obtain && goals.some((g) => o.core.includes(g.id) || o.extra.includes(g.id)))
    .sort((a, b) => b.tier - a.tier)
    .slice(0, 8 + (i % 4))
    .map((o, k) => ({
      name: o.name,
      ...(k < 2 && { key: true }),
      ...(k < 2 && { equipment: [{ name: eqs[(i * 7 + k * 3) % eqs.length], priority: true }] }),
    }))
  const t = new Date(Date.now() - i * 5.3 * 3600_000).toISOString()
  return {
    id: `demo-${i}`,
    code: '',
    lineup: { name, goals, members, equipment: i % 2 ? [eqs[i + 20]] : [], strategyId: strategies[(i * 9) % strategies.length] },
    description,
    author: { id: 'demo', nickname: ['罗德岛干员', '叙拉古家族成员', '炎客', '萨尔贡流沙', '雪境来客', '玩家114514'][i % 6] },
    createdAt: t,
    updatedAt: t,
    featured: i === 1 || i === 4,
    likes: [128, 64, 33, 12, 7, 2][i % 6],
    imports: [420, 233, 91, 40, 18, 5][i % 6],
    comments: [12, 5, 0, 3, 1, 0][i % 6],
    liked: i === 2,
    favorited: i === 1,
    current: i !== 7,
  }
}

const posts: Post[] = [
  build(0, '叙拉古 · 家族叠层', [{ id: cov('叙拉古'), level: 2 }, { id: cov('坚守'), level: 1 }], '前期先抓德克萨斯和拉普兰德，四本左右凑齐叙拉古 6 人。'),
  build(1, '炎 · 元素爆发', [{ id: cov('炎'), level: 2 }], '炎 6 人后元素损伤很高，配合远见打首领很稳。'),
  build(2, '莱塔尼亚 · 法术叠层', [{ id: cov('莱塔尼亚'), level: 1 }, { id: cov('远见'), level: 1 }], ''),
  build(3, '萨尔贡 · 流沙经济', [{ id: cov('萨尔贡'), level: 1 }, { id: cov('投资人'), level: 1 }], '经济流，前期拉利息，中期一口气升本。'),
  build(4, '谢拉格 · 不融冰', [{ id: cov('谢拉格'), level: 2 }, { id: cov('协防干员'), level: 1 }], '冰冻控场。'),
  build(5, '乌萨斯 · 硬扛', [{ id: cov('乌萨斯'), level: 2 }], ''),
  build(6, '卡西米尔 · 骑士冲锋', [{ id: cov('卡西米尔'), level: 1 }, { id: cov('坚守'), level: 1 }], ''),
  build(7, '上赛季的维多利亚', [{ id: cov('维多利亚'), level: 1 }], '旧赛季留下的，看看就好。'),
]

/** 只接列表、详情、导入计数、评论列表；其余照旧走线上（演示时用不到） */
export async function demoReq(method: string, path: string): Promise<unknown> {
  const [p, qs] = path.split('?')
  if (method === 'GET' && (p === '/public/posts' || p === '/posts')) {
    const q = new URLSearchParams(qs)
    const kw = q.get('q') ?? ''
    const covs = q.getAll('covenant')
    const list = posts.filter((x) => (!kw || JSON.stringify([x.lineup, x.author.nickname]).includes(kw)) && covs.every((c) => x.lineup.goals.some((g) => g.id === c)) && (q.get('sort') !== 'featured' || x.featured))
    return { posts: q.get('favorite') ? list.filter((x) => x.favorited) : list, cursor: null }
  }
  const m = p.match(/^\/posts\/([^/]+)(\/\w+)?$/)
  const post = posts.find((x) => x.id === decodeURIComponent(m?.[1] ?? ''))
  if (post && !m![2]) return post
  if (post && m![2] === '/import') return { imports: ++post.imports }
  if (post && m![2] === '/comments') return { comments: [], cursor: null }
  return undefined
}
