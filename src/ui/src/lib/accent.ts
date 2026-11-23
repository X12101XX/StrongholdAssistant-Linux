// 通用风格主题（毛玻璃、圆润）的自定义主题色（v0.9.8）：用户选一个颜色，按深浅配色换算成强调色令牌。
// 深色底上颜色太暗看不清，提亮到够亮；浅色底上太亮（黄、浅青）白底上看不清，压暗到够深。
// 按钮上的字（--mint-ink）按强调色的亮度选深字或白字。

/** 设置里的预设色块；'' 是主题自带的颜色（毛玻璃蓝、圆润紫） */
export const ACCENTS: { v: string; name: string }[] = [
  { v: '', name: '主题默认' },
  { v: '#3b82f6', name: '蓝' },
  { v: '#8b5cf6', name: '紫' },
  { v: '#ec4899', name: '粉' },
  { v: '#ef4444', name: '红' },
  { v: '#f97316', name: '橙' },
  { v: '#eab308', name: '黄' },
  { v: '#22c55e', name: '绿' },
  { v: '#14b8a6', name: '青' },
  { v: '#64748b', name: '石墨' },
]

type Hsl = [number, number, number]

function hexToHsl(hex: string): Hsl {
  const n = parseInt(hex.slice(1), 16)
  const r = (n >> 16) / 255, g = ((n >> 8) & 255) / 255, b = (n & 255) / 255
  const max = Math.max(r, g, b), min = Math.min(r, g, b)
  const l = (max + min) / 2
  if (max === min) return [0, 0, l]
  const d = max - min
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min)
  const h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4
  return [h / 6, s, l]
}

function hslToRgb([h, s, l]: Hsl): [number, number, number] {
  const f = (n: number) => {
    const k = (n + h * 12) % 12
    const a = s * Math.min(l, 1 - l)
    return Math.round(255 * (l - a * Math.max(-1, Math.min(k - 3, 9 - k, 1))))
  }
  return [f(0), f(8), f(4)]
}

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v))
const css = (rgb: number[]) => `rgb(${rgb.join(' ')})`
/** 相对亮度（WCAG） */
function luminance([r, g, b]: number[]) {
  const c = (v: number) => ((v /= 255) <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4)
  return 0.2126 * c(r) + 0.7152 * c(g) + 0.0722 * c(b)
}

/** 换算成强调色令牌；hi 是更醒目的一档（深色里更亮，浅色里更深），lo 是更弱的一档 */
export function accentTokens(hex: string, scheme: 'dark' | 'light') {
  const [h, s, l] = hexToHsl(hex)
  const dark = scheme === 'dark'
  const base: Hsl = [h, s, dark ? clamp(l, 0.62, 0.78) : clamp(l, 0.32, 0.46)]
  const rgb = hslToRgb(base)
  const hi = hslToRgb([h, s, clamp(base[2] + (dark ? 0.1 : -0.08), 0, 1)])
  const lo = hslToRgb([h, s, clamp(base[2] + (dark ? -0.14 : 0.16), 0, 1)])
  // 白字对比够 3:1 就用白字，否则用深字
  const ink = 1.05 / (luminance(rgb) + 0.05) >= 3 ? '#ffffff' : css(hslToRgb([h, Math.min(s, 0.6), 0.1]))
  return { '--mint-rgb': rgb.join(' '), '--mint-hi': css(hi), '--mint-lo': css(lo), '--mint-ink': ink }
}

export const ACCENT_KEYS = ['--mint-rgb', '--mint-hi', '--mint-lo', '--mint-ink'] as const
