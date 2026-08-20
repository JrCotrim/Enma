import type { LegalDocumentMetadata } from './documentTypes'

const fileTypeLabels: Readonly<Record<string, string>> = {
  'application/pdf': 'PDF',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document':
    'Documento do Word',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet':
    'Planilha do Excel',
  'image/png': 'Imagem PNG',
  'image/jpeg': 'Imagem JPEG',
}

export function formatDocumentFileType(contentType: string): string {
  return fileTypeLabels[contentType.toLowerCase()] ?? contentType
}

export function formatDocumentSize(sizeBytes: number): string {
  if (sizeBytes < 1024) {
    return `${sizeBytes} B`
  }

  const units = ['KB', 'MB', 'GB'] as const
  let value = sizeBytes / 1024
  let unitIndex = 0

  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024
    unitIndex += 1
  }

  return `${new Intl.NumberFormat('pt-BR', {
    maximumFractionDigits: value >= 10 ? 1 : 2,
  }).format(value)} ${units[unitIndex]}`
}

export function formatDocumentCreatedAt(value: string): string {
  return new Intl.DateTimeFormat('pt-BR', {
    dateStyle: 'short',
    timeStyle: 'short',
  }).format(new Date(value))
}

export function getDocumentContextLabel(
  document: LegalDocumentMetadata,
): string {
  if (document.processId) {
    return 'Vinculado a processo'
  }

  if (document.clientId) {
    return 'Vinculado a cliente'
  }

  return 'Documento geral'
}
