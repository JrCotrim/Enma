import { useState } from 'react'
import { ClientRequestError } from './clientService'
import type {
  ClientDetail,
  ClientPersonType,
  UpdateClientRequest,
} from './clientTypes'

export const maximumClientNameLength = 150
export const maximumClientAddressLength = 300
export const maximumClientNotesLength = 2_000

export type ClientFormField =
  | 'name'
  | 'email'
  | 'phone'
  | 'document'
  | 'address'
  | 'notes'

export type ClientFormErrors = Partial<Record<ClientFormField, string>>

export interface ClientFormValues {
  readonly personType: ClientPersonType
  readonly name: string
  readonly email: string
  readonly phone: string
  readonly document: string
  readonly address: string
  readonly notes: string
}

export const emptyClientFormValues: ClientFormValues = {
  personType: 'individual',
  name: '',
  email: '',
  phone: '',
  document: '',
  address: '',
  notes: '',
}

export function clientFormValuesFrom(client: ClientDetail): ClientFormValues {
  return {
    personType: client.personType,
    name: client.name,
    email: client.email ?? '',
    phone: client.phone ?? '',
    document:
      (client.personType === 'company' ? client.cnpj : client.cpf) ?? '',
    address: client.address ?? '',
    notes: client.notes ?? '',
  }
}

function normalizeOptionalClientField(value: string): string | null {
  const trimmed = value.trim()
  return trimmed.length === 0 ? null : trimmed
}

export function validateClientName(values: ClientFormValues): string | undefined {
  const trimmedName = values.name.trim()
  const isCompany = values.personType === 'company'

  if (trimmedName.length === 0) {
    return isCompany ? 'Informe a razão social.' : 'Informe o nome do cliente.'
  }

  if (trimmedName.length > maximumClientNameLength) {
    return isCompany
      ? `A razão social deve ter no máximo ${maximumClientNameLength} caracteres.`
      : `O nome deve ter no máximo ${maximumClientNameLength} caracteres.`
  }

  return undefined
}

// The document of the other person type is always sent as null.
export function toClientRequest(values: ClientFormValues): UpdateClientRequest {
  const document = normalizeOptionalClientField(values.document)

  return {
    name: values.name.trim(),
    email: normalizeOptionalClientField(values.email),
    phone: normalizeOptionalClientField(values.phone),
    cpf: values.personType === 'individual' ? document : null,
    personType: values.personType,
    cnpj: values.personType === 'company' ? document : null,
    address: normalizeOptionalClientField(values.address),
    notes: normalizeOptionalClientField(values.notes),
  }
}

export function getClientFieldErrors(
  error: unknown,
  personType: ClientPersonType,
): ClientFormErrors | undefined {
  if (!(error instanceof ClientRequestError)) {
    return undefined
  }

  if (error.failure === 'duplicate-document') {
    return {
      document:
        personType === 'company'
          ? 'Já existe um cliente com este CNPJ neste escritório.'
          : 'Já existe um cliente com este CPF neste escritório.',
    }
  }

  if (error.failure !== 'bad-request') {
    return undefined
  }

  switch (error.field) {
    case 'name':
      return {
        name: personType === 'company' ? 'Razão social inválida.' : 'Nome inválido.',
      }
    case 'email':
      return { email: 'E-mail inválido.' }
    case 'phone':
      return { phone: 'Telefone inválido.' }
    case 'cpf':
      return { document: 'CPF inválido.' }
    case 'cnpj':
      return { document: 'CNPJ inválido.' }
    case 'address':
      return {
        address: `O endereço deve ter no máximo ${maximumClientAddressLength} caracteres.`,
      }
    case 'notes':
      return {
        notes: `As observações devem ter no máximo ${maximumClientNotesLength} caracteres.`,
      }
    default:
      return undefined
  }
}

export interface ClientFormState {
  readonly values: ClientFormValues
  readonly errors: ClientFormErrors
  readonly setErrors: (errors: ClientFormErrors) => void
  readonly changeField: (field: ClientFormField, value: string) => void
  readonly changePersonType: (personType: ClientPersonType) => void
  readonly reset: (values?: ClientFormValues) => void
}

export function useClientForm(): ClientFormState {
  const [values, setValues] = useState(emptyClientFormValues)
  const [errors, setErrors] = useState<ClientFormErrors>({})

  return {
    values,
    errors,
    setErrors,
    changeField(field, value) {
      setValues((current) => ({ ...current, [field]: value }))
      setErrors((current) => ({ ...current, [field]: undefined }))
    },
    changePersonType(personType) {
      if (values.personType === personType) {
        return
      }

      // Switching the type discards the typed document instead of
      // reinterpreting it as the other kind.
      setValues((current) => ({ ...current, personType, document: '' }))
      setErrors((current) => ({
        ...current,
        name: undefined,
        document: undefined,
      }))
    },
    reset(nextValues = emptyClientFormValues) {
      setValues(nextValues)
      setErrors({})
    },
  }
}
