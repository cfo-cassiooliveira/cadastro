import { ApiFailure, BatchStatus, LineStatus } from './user-import.model';

/** Mensagem pronta para a tela; `needsOtp` indica que o usuário precisa confirmar o código do autenticador. */
export interface FailureMessage {
  readonly text: string;
  readonly needsOtp: boolean;
}

const OTP_TEXT = $localize`Confirme sua identidade com o código do autenticador para continuar. Depois de confirmar, você volta a esta tela.`;
const GENERIC_TEXT = $localize`Não foi possível concluir agora. Tente novamente em instantes.`;
const TOO_LARGE_TEXT = $localize`O arquivo passa de 5 MB. Divida a lista em arquivos menores.`;

/**
 * Traduz a falha da API (por status e código, nunca por texto do servidor) para uma mensagem em PT-BR. A API só devolve
 * 403 sem código quando falta o OTP recente (a role o guard de rota já garante).
 */
export function describeFailure(failure: ApiFailure): FailureMessage {
  if (failure.status === 403 && failure.code !== 'import.not_owner') {
    return { text: OTP_TEXT, needsOtp: true };
  }
  return { text: messageFor(failure), needsOtp: false };
}

function messageFor(failure: ApiFailure): string {
  switch (failure.code) {
    case 'file.too_large':
      return TOO_LARGE_TEXT;
    case 'file.header_invalid':
      return $localize`O cabeçalho do arquivo não está no formato esperado. A primeira linha deve ser nome,email,cpf,telefone.`;
    case 'file.too_many_lines':
      return $localize`O arquivo tem mais de 5.000 linhas. Divida a lista em arquivos menores.`;
    case 'file.infected':
      return $localize`O arquivo foi recusado pela verificação de segurança. Envie outro arquivo.`;
    case 'scanner.unavailable':
      return $localize`A verificação de segurança do arquivo está indisponível. Tente novamente em instantes.`;
    case 'import.no_valid_lines':
      return $localize`Nenhuma linha do arquivo é válida. Corrija os problemas abaixo e envie de novo.`;
    case 'import.duplicate_file':
      return $localize`Este arquivo já foi enviado.`;
    case 'import.file_required':
    case 'import.multipart_required':
    case 'import.multipart_invalid':
      return $localize`Não foi possível ler o arquivo enviado. Selecione o CSV de novo.`;
    case 'import.confirmation_token_invalid':
      return $localize`A confirmação não corresponde a este resumo. Envie o arquivo de novo para gerar outro resumo.`;
    case 'import.invalid_state':
      return $localize`O estado do lote não permite esta ação. Atualize a página para ver a situação atual.`;
    case 'import.concurrent_update':
      return $localize`O lote foi alterado por outra requisição. Tente novamente.`;
    case 'import.not_owner':
      return $localize`Só quem criou o lote pode confirmar, cancelar ou reprocessar.`;
    case 'import.not_found':
      return $localize`Lote não encontrado.`;
  }
  if (failure.status === 429) {
    return $localize`Limite de envios atingido. Tente novamente mais tarde.`;
  }
  return failure.status === 413 ? TOO_LARGE_TEXT : GENERIC_TEXT;
}

/** Motivo de um problema por linha do CSV (códigos `row.*` da API). */
export function describeRowIssue(code: string): string {
  switch (code) {
    case 'row.name_required':
      return $localize`Nome não informado.`;
    case 'row.name_invalid':
      return $localize`Nome inválido ou longo demais.`;
    case 'row.email_required':
      return $localize`E-mail não informado.`;
    case 'row.email_invalid':
      return $localize`E-mail inválido.`;
    case 'row.cpf_required':
      return $localize`CPF não informado.`;
    case 'row.cpf_invalid':
      return $localize`CPF inválido.`;
    case 'row.phone_invalid':
      return $localize`Telefone inválido.`;
    case 'row.duplicate_email':
      return $localize`E-mail repetido no arquivo.`;
    case 'row.duplicate_cpf':
      return $localize`CPF repetido no arquivo.`;
    case 'row.malformed':
      return $localize`Linha fora do formato (número de colunas incorreto).`;
    case 'row.field_too_long':
      return $localize`Um dos campos é longo demais.`;
    default:
      return $localize`Linha com problema (código ${code}:CODE:).`;
  }
}

/**
 * Mensagens por código de falha definitiva de uma linha (`ImportFailureCodes` do backend). Para tratar um código novo do
 * consumidor, inclua-o aqui e em `KNOWN_LINE_FAILURE_CODES`; o teste falha se algum conhecido ficar sem mensagem própria.
 */
const LINE_FAILURE_MESSAGES: Readonly<Record<string, string>> = {
  'identity.unavailable': $localize`O serviço de identidade estava indisponível. Reprocesse em alguns minutos.`,
  'identity.rejected': $localize`O serviço de identidade recusou este cadastro. Confira os dados da linha.`,
  'identity.activation_email_rejected': $localize`O acesso foi criado, mas o e-mail de ativação não pôde ser enviado. Reprocesse.`,
  'processing.retries_exhausted': $localize`Esgotamos as tentativas automáticas. Reprocesse mais tarde.`,
  'processing.failed': $localize`Ocorreu uma falha inesperada ao processar esta linha. Reprocesse; se repetir, procure o suporte.`,
  'message.invalid': $localize`O pedido de processamento desta linha estava inválido. Reprocesse; se repetir, procure o suporte.`,
  'line.not_found': $localize`Esta linha não foi encontrada no processamento. Reprocesse; se repetir, procure o suporte.`,
  'personal_data.unavailable': $localize`Os dados desta linha não estão mais disponíveis. Envie a pessoa de novo em um novo arquivo.`,
  'batch.invalid_state': $localize`O lote não estava em processamento quando esta linha foi tratada. Reprocesse.`,
  'db.concurrency_conflict': $localize`Várias atualizações ao mesmo tempo impediram de salvar o resultado. Reprocesse.`,
  // Compatibilidade com códigos antigos.
  'keycloak.create_failed': $localize`O acesso não pôde ser criado no serviço de identidade. Tente reprocessar.`,
};

/** Códigos de falha que o backend emite hoje (cada um precisa ter mensagem própria). */
export const KNOWN_LINE_FAILURE_CODES: readonly string[] = [
  'identity.unavailable',
  'identity.rejected',
  'identity.activation_email_rejected',
  'processing.retries_exhausted',
  'processing.failed',
  'message.invalid',
  'line.not_found',
  'personal_data.unavailable',
  'batch.invalid_state',
  'db.concurrency_conflict',
];

/**
 * Códigos de falha que NÃO admitem reprocesso: o backend só reabre linha com erro cujos dados pessoais ainda estão
 * guardados (`ImportLine.Reprocess`), e `personal_data.unavailable` é justamente a falha de dados apagados ou ilegíveis.
 * Os demais códigos conhecidos mantêm os dados e podem ser reprocessados; código novo/desconhecido segue permitido e o
 * servidor continua sendo a autoridade (ele pula as linhas sem dados).
 */
export const NON_REPROCESSABLE_LINE_FAILURE_CODES: readonly string[] = [
  'personal_data.unavailable',
];

/** Se a tela deve oferecer "Reprocessar" para a linha com este código de falha. */
export function isLineReprocessable(code: string | null): boolean {
  return code === null || !NON_REPROCESSABLE_LINE_FAILURE_CODES.includes(code);
}

/** Se o código tem mensagem própria (e não cai no texto genérico). */
export function hasLineFailureMessage(code: string): boolean {
  return code in LINE_FAILURE_MESSAGES;
}

const LINE_FAILURE_FALLBACK = $localize`Não foi possível criar o acesso. Tente reprocessar ou procure o suporte.`;

/** Aviso curto, sem alarme, sobre a marcação do login no Legado (só quando há algo a dizer). */
export function describeLegacyMark(status: string): string | null {
  switch (status) {
    case 'NoCandidate':
      return $localize`Sem login correspondente no Legado.`;
    case 'Ambiguous':
      return $localize`Login do Legado não marcado: há mais de um com este e-mail.`;
    case 'Failed':
      return $localize`Login do Legado não marcado agora. O acesso foi criado.`;
    default:
      return null;
  }
}

/** Motivo da falha definitiva de uma linha processada, em português e sem código técnico. */
export function describeLineFailure(code: string | null): string {
  return (code && LINE_FAILURE_MESSAGES[code]) || LINE_FAILURE_FALLBACK;
}

export function batchStatusLabel(status: BatchStatus): string {
  switch (status) {
    case 'AwaitingConfirmation':
      return $localize`Aguardando confirmação`;
    case 'Processing':
      return $localize`Processando`;
    case 'Completed':
      return $localize`Concluído`;
    case 'CompletedWithErrors':
      return $localize`Concluído com erros`;
    case 'Cancelled':
      return $localize`Cancelado`;
  }
}

export function lineStatusLabel(status: LineStatus): string {
  switch (status) {
    case 'Pending':
      return $localize`Na fila`;
    case 'Created':
      return $localize`Criado`;
    case 'AlreadyExists':
      return $localize`Já existia`;
    case 'Error':
      return $localize`Com erro`;
    case 'Cancelled':
      return $localize`Cancelado`;
  }
}

/** Tom visual do selo de estado (sempre acompanhado do texto). */
export type Tone = 'success' | 'danger' | 'info' | 'neutral';

export function batchTone(status: BatchStatus): Tone {
  switch (status) {
    case 'Completed':
      return 'success';
    case 'CompletedWithErrors':
      return 'danger';
    case 'AwaitingConfirmation':
    case 'Processing':
      return 'info';
    case 'Cancelled':
      return 'neutral';
  }
}

export function lineTone(status: LineStatus): Tone {
  switch (status) {
    case 'Created':
    case 'AlreadyExists':
      return 'success';
    case 'Error':
      return 'danger';
    case 'Pending':
      return 'info';
    case 'Cancelled':
      return 'neutral';
  }
}
