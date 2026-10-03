import {
  maximumClientAddressLength,
  maximumClientNameLength,
  maximumClientNotesLength,
  type ClientFormField,
  type ClientFormState,
} from './clientForm'

function toAsciiUpperCase(value: string): string {
  // Non-ASCII letters are left as typed so the backend can reject them.
  return value.replace(/[a-z]/g, (letter) => letter.toUpperCase())
}

export function ClientFormFields({
  idPrefix,
  form,
}: {
  readonly idPrefix: string
  readonly form: ClientFormState
}) {
  const { values, errors, changeField, changePersonType } = form
  const isCompany = values.personType === 'company'

  function errorId(field: ClientFormField): string {
    return `${idPrefix}-${field}-error`
  }

  function fieldErrorProps(field: ClientFormField) {
    return errors[field]
      ? { 'aria-describedby': errorId(field), 'aria-invalid': true }
      : {}
  }

  function renderError(field: ClientFormField) {
    return errors[field] ? (
      <p id={errorId(field)} className="form-error" role="alert">
        {errors[field]}
      </p>
    ) : null
  }

  return (
    <>
      <fieldset className="client-person-type">
        <legend>Tipo de pessoa</legend>
        <button
          className="secondary-button"
          type="button"
          aria-pressed={!isCompany}
          onClick={() => changePersonType('individual')}
        >
          Pessoa física
        </button>
        <button
          className="secondary-button"
          type="button"
          aria-pressed={isCompany}
          onClick={() => changePersonType('company')}
        >
          Pessoa jurídica
        </button>
      </fieldset>

      <label htmlFor={`${idPrefix}-name`}>{isCompany ? 'Razão social' : 'Nome'}</label>
      <input
        id={`${idPrefix}-name`}
        name="name"
        value={values.name}
        maxLength={maximumClientNameLength}
        onChange={(event) => changeField('name', event.target.value)}
        {...fieldErrorProps('name')}
        autoFocus
        required
      />
      {renderError('name')}

      <label htmlFor={`${idPrefix}-email`}>E-mail</label>
      <input
        id={`${idPrefix}-email`}
        name="email"
        type="email"
        value={values.email}
        maxLength={254}
        autoComplete="email"
        onChange={(event) => changeField('email', event.target.value)}
        {...fieldErrorProps('email')}
      />
      {renderError('email')}

      <label htmlFor={`${idPrefix}-phone`}>Telefone</label>
      <input
        id={`${idPrefix}-phone`}
        name="phone"
        type="tel"
        value={values.phone}
        autoComplete="tel"
        onChange={(event) => changeField('phone', event.target.value)}
        {...fieldErrorProps('phone')}
      />
      {renderError('phone')}

      <label htmlFor={`${idPrefix}-document`}>{isCompany ? 'CNPJ' : 'CPF'}</label>
      <input
        id={`${idPrefix}-document`}
        name={isCompany ? 'cnpj' : 'cpf'}
        type="text"
        value={values.document}
        inputMode={isCompany ? 'text' : 'numeric'}
        autoCapitalize={isCompany ? 'characters' : 'off'}
        autoComplete="off"
        spellCheck={false}
        onChange={(event) =>
          changeField(
            'document',
            isCompany ? toAsciiUpperCase(event.target.value) : event.target.value,
          )
        }
        {...fieldErrorProps('document')}
      />
      {renderError('document')}

      <label htmlFor={`${idPrefix}-address`}>Endereço</label>
      <input
        id={`${idPrefix}-address`}
        name="address"
        type="text"
        value={values.address}
        maxLength={maximumClientAddressLength}
        autoComplete="street-address"
        onChange={(event) => changeField('address', event.target.value)}
        {...fieldErrorProps('address')}
      />
      {renderError('address')}

      <label htmlFor={`${idPrefix}-notes`}>Observações</label>
      <textarea
        id={`${idPrefix}-notes`}
        name="notes"
        rows={4}
        value={values.notes}
        maxLength={maximumClientNotesLength}
        onChange={(event) => changeField('notes', event.target.value)}
        {...fieldErrorProps('notes')}
      />
      {renderError('notes')}
    </>
  )
}
