const dateFormatter = new Intl.DateTimeFormat('pt-BR', {
  dateStyle: 'short',
  timeStyle: 'short',
})

export function formatClientCreatedAt(createdAt: string): string {
  return dateFormatter.format(new Date(createdAt))
}

const normalizedCpfPattern = /^\d{11}$/
const normalizedCnpjPattern = /^[0-9A-Z]{12}\d{2}$/

// Display-only masks for the normalized values returned by the API; any other
// shape is shown unchanged. Check-digit validation stays on the backend.
export function formatCpf(cpf: string): string {
  return normalizedCpfPattern.test(cpf)
    ? `${cpf.slice(0, 3)}.${cpf.slice(3, 6)}.${cpf.slice(6, 9)}-${cpf.slice(9)}`
    : cpf
}

const nationalPhonePattern = /^\d{10,11}$/
const brazilianPhonePattern = /^55\d{10,11}$/

function formatNationalPhone(digits: string): string {
  const splitAt = digits.length - 4
  return `(${digits.slice(0, 2)}) ${digits.slice(2, splitAt)}-${digits.slice(splitAt)}`
}

export function formatPhone(phone: string): string {
  if (nationalPhonePattern.test(phone)) {
    return formatNationalPhone(phone)
  }

  return brazilianPhonePattern.test(phone)
    ? `+55 ${formatNationalPhone(phone.slice(2))}`
    : phone
}

export function formatCnpj(cnpj: string): string {
  return normalizedCnpjPattern.test(cnpj)
    ? `${cnpj.slice(0, 2)}.${cnpj.slice(2, 5)}.${cnpj.slice(5, 8)}/${cnpj.slice(8, 12)}-${cnpj.slice(12)}`
    : cnpj
}
