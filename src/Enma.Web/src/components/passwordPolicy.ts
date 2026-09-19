export const minimumPasswordLength = 8
export const maximumPasswordLength = 128

const uppercaseLetterPattern = /\p{Lu}/u
const lowercaseLetterPattern = /\p{Ll}/u
const numberPattern = /\p{Nd}/u

export interface PasswordPolicyStatus {
  readonly minimumLength: boolean
  readonly withinMaximumLength: boolean
  readonly hasNonWhitespace: boolean
  readonly hasUppercase: boolean
  readonly hasLowercase: boolean
  readonly hasNumber: boolean
}

export function getPasswordPolicyStatus(password: string): PasswordPolicyStatus {
  return {
    minimumLength: password.length >= minimumPasswordLength,
    withinMaximumLength: password.length <= maximumPasswordLength,
    hasNonWhitespace: password.trim().length > 0,
    hasUppercase: uppercaseLetterPattern.test(password),
    hasLowercase: lowercaseLetterPattern.test(password),
    hasNumber: numberPattern.test(password),
  }
}

export function getPasswordPolicyError(password: string): string | undefined {
  const status = getPasswordPolicyStatus(password)

  if (!status.hasNonWhitespace) {
    return password.length === 0
      ? 'Preencha este campo.'
      : 'A senha não pode conter apenas espaços.'
  }

  if (!status.minimumLength) {
    return `Use pelo menos ${minimumPasswordLength} caracteres.`
  }

  if (!status.withinMaximumLength) {
    return `Use no máximo ${maximumPasswordLength} caracteres.`
  }

  if (!status.hasUppercase) {
    return 'Inclua pelo menos uma letra maiúscula.'
  }

  if (!status.hasLowercase) {
    return 'Inclua pelo menos uma letra minúscula.'
  }

  if (!status.hasNumber) {
    return 'Inclua pelo menos um número.'
  }

  return undefined
}
