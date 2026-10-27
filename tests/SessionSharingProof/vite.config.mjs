import frontend from '../../frontend/vite.config.js'
export default ({ mode }) => {
  const config = frontend({ mode })
  config.root = new URL('../../frontend', import.meta.url).pathname
  config.server.proxy['/__identity'] = { target: 'http://127.0.0.1:8090', rewrite: path => path.replace(/^\/__identity/, '') }
  return config
}
