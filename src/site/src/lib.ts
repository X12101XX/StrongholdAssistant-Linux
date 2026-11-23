import shotWidths from './assets/shots/widths.json'
// 进入视口时加 .in（配合 site.css 的 .rv），只触发一次
const io =
  typeof IntersectionObserver === 'undefined'
    ? null
    : new IntersectionObserver(
        (entries) => {
          for (const e of entries) {
            if (!e.isIntersecting) continue
            e.target.classList.add('in')
            io!.unobserve(e.target)
          }
        },
        { rootMargin: '0px 0px -8% 0px' },
      )

export function reveal(node: HTMLElement, delay = 0) {
  node.classList.add('rv')
  if (delay) node.style.setProperty('--d', `${delay}ms`)
  if (!io) node.classList.add('in')
  else io.observe(node)
  return { destroy: () => io?.unobserve(node) }
}

// 界面截图（scripts/shot-sizes.py 生成几档宽度）：快滚到时才加载，按图片实际显示宽度挑一档。
// 手机里的 QQ、微信内置浏览器不认 loading="lazy"，所以自己判断；隐藏的（display: none）不会加载
const shotFiles = import.meta.glob('./assets/shots/*.webp', { eager: true, import: 'default' }) as Record<string, string>
const srcset = (name: string) => {
  const widths = (shotWidths as Record<string, number[]>)[name]
  const full = widths.at(-1)
  return widths.map((w) => `${shotFiles[`./assets/shots/${name}${w === full ? '' : '-' + w}.webp`]} ${w}w`).join(', ')
}
// 手机 3 倍屏按 1.5 倍挑（360 宽的图挑 560 档而不是 760 原图），省近一半，肉眼差别不大
const density = devicePixelRatio > 2 ? 1.5 : devicePixelRatio
const load = (img: HTMLImageElement) => {
  // 没写宽高的图加载前宽度是 0，按外框宽度算
  const width = img.getBoundingClientRect().width || img.parentElement!.getBoundingClientRect().width
  img.sizes = `${Math.ceil((width * density) / devicePixelRatio)}px`
  img.srcset = img.dataset.srcset!
}
const near =
  typeof IntersectionObserver === 'undefined'
    ? null
    : new IntersectionObserver(
        (entries) => {
          for (const e of entries) {
            if (!e.isIntersecting) continue
            load(e.target as HTMLImageElement)
            near!.unobserve(e.target)
          }
        },
        { rootMargin: '600px' },
      )

export function shot(img: HTMLImageElement, name: string) {
  img.dataset.srcset = srcset(name)
  if (!near) load(img)
  else near.observe(img)
  return { destroy: () => near?.unobserve(img) }
}
