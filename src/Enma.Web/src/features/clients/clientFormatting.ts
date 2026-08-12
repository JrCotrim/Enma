const dateFormatter = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

export function formatClientCreatedAt(createdAt: string): string {
  return dateFormatter.format(new Date(createdAt))
}
