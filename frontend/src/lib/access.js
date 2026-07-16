export function sessionCapabilities(session) {
  switch (session?.accessRole) {
    case 'Viewer':
      return { canWrite: false, canManage: false, canShell: false }
    case 'Collaborator':
      return { canWrite: true, canManage: false, canShell: false }
    default:
      return { canWrite: true, canManage: true, canShell: true }
  }
}
