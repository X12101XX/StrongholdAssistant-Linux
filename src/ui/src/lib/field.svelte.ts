// 场上与小棋盘（任务 169）：识别出的场上状态、地图与本回合路线、站位推荐，加玩家在小棋盘上自己拖人推演的本地布局。
// 推演只在界面里，不动游戏、不存盘；一键回到识别结果
import { api } from './api'
import { app } from './state.svelte'
import type { BoardView, FieldCell, FieldState, FieldUnit, PlacementPlan } from './api'

export const cellKey = (c: FieldCell) => `${c.zone}:${c.col}:${c.row}`
export const sameCell = (a: FieldCell, b: FieldCell) => a.zone === b.zone && a.col === b.col && a.row === b.row
/** 棋盘格的地形字母（BoardView.cells：X 不能放、G 地面、H 高台、T 特殊地面、D 防守点、S 出生点）；没有数据为 null */
export const terrainOf = (b: BoardView | null, col: number, row: number) => b?.cells?.[row]?.[col + 2] ?? null
/** 能放人的格子：整备区 1～10；棋盘按 cells 里 G / H / T，没有数据时列 0～6、行 0～3 */
export const placeable = (b: BoardView | null, c: FieldCell) => {
  if (c.zone === 'bench') return c.col >= 1 && c.col <= 10
  if (c.col < 0 || c.col > 6 || c.row < 0 || c.row > 3) return false
  const t = terrainOf(b, c.col, c.row)
  return t === null || 'GHT'.includes(t)
}

// 旧宿主没有 board / placement（任务 169 之前）时当作没有，不挡住界面启动
const [field0, board0, plan0] = await Promise.all([api.field(), api.board().catch(() => null), api.placement().catch(() => null)])

class FieldStore {
  state = $state<FieldState | null>(field0)
  board = $state<BoardView | null>(board0)
  plan = $state<PlacementPlan | null>(plan0)
  /** 推演：识别结果里的格子（cellKey）→ 挪到的格子；空 = 没在推演 */
  moves = $state<Record<string, FieldCell>>({})
  /** 正在手动识别（「识别」按钮或快捷键） */
  recognizing = $state(false)
  /** 识别后站位推荐还在后台算（任务 272 起名字先出、推荐后到；收到 placement 推送就结束） */
  planPending = $state(false)

  units = $derived(this.state?.units ?? [])
  pending = $derived(this.units.filter((u) => u.status === 'pending'))
  /** 推演后的布局：每个识别出的人现在站在哪（没挪过的在原位） */
  layout = $derived(this.units.map((u) => ({ unit: u, at: this.moves[cellKey(u)] ?? (u as FieldCell) })))
  sandbox = $derived(this.layout.some((x) => !sameCell(x.at, x.unit)))
  /** 推荐是不是依据当前的场上状态算的 */
  planStale = $derived(!!this.plan && !!this.state && this.plan.basedOn !== this.state.updatedAt)

  constructor() {
    api.onField((f) => {
      this.state = f
      // 推演里挪过的人，识别结果里已经不在原格的（被拖走、卖掉）就不再跟踪
      const keys = new Set(f.units.map(cellKey))
      this.moves = Object.fromEntries(Object.entries(this.moves).filter(([k]) => keys.has(k)))
    })
    api.onBoard((b) => (this.board = b))
    api.onPlacement((p) => {
      this.plan = p
      this.planPending = false
    })
    api.onFieldHotkey(() => this.recognize(true))
  }

  /** 认一次场上，结果经 onField / onPlacement 推回来；失败把原因用提示条显示。快捷键触发时成功也提示一下（人在看游戏） */
  async recognize(byKey = false) {
    if (this.recognizing) return
    this.recognizing = true
    this.planPending = true
    try {
      const f = await api.recognizeField()
      if (byKey) app.showToast(`场上已识别：${f.units.length} 人`)
    } catch (e) {
      this.planPending = false
      app.showToast((e as Error).message, true)
    } finally {
      this.recognizing = false
    }
  }

  /** 推演：把 from 格上的人挪到 to；to 上有人就互换 */
  move(from: FieldUnit, to: FieldCell) {
    const at = this.moves[cellKey(from)] ?? from
    if (sameCell(at, to)) return
    const other = this.layout.find((x) => sameCell(x.at, to))
    const next = { ...this.moves, [cellKey(from)]: { zone: to.zone, col: to.col, row: to.row } }
    if (other) next[cellKey(other.unit)] = { zone: at.zone, col: at.col, row: at.row }
    // 回到原位的不记
    this.moves = Object.fromEntries(Object.entries(next).filter(([k, c]) => cellKey(c) !== k))
  }

  /** 照推荐摆：推荐里的人都挪到推荐的格子（按 from 认人），没算进去的人不动 */
  applyPlan() {
    if (!this.plan) return
    this.moves = Object.fromEntries(this.plan.moves.filter((m) => !sameCell(m.from, m.to)).map((m) => [cellKey(m.from), m.to]))
  }

  reset() {
    this.moves = {}
  }

  async confirm(cell: FieldCell, operator: string | null) {
    this.state = await api.setFieldOperator(cell, operator)
  }
}

export const field = new FieldStore()
