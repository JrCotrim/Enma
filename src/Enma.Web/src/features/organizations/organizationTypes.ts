export type OrganizationRole = 'Owner' | 'Administrator' | 'Member'

export interface OrganizationNavigationItem {
  readonly id: string
  readonly name: string
  readonly role: OrganizationRole
}

export function getOrganizationRoleLabel(role: OrganizationRole): string {
  switch (role) {
    case 'Owner':
      return 'Proprietário'
    case 'Administrator':
      return 'Administrador'
    case 'Member':
      return 'Membro'
  }
}
