package cupoftea.core

/** Сведения о программе (как Product.cs на десктопе). */
object Product {
    /** Короткое имя: в User-Agent, в имени автовыбора, в заголовке x-client. */
    const val ID = "CupOfTea"
    const val NAME = "The Cup of Tea"

    /** Репозиторий релизов на GitHub; ТВ-версии — с тегами tv-v<версия>. */
    const val UPDATE_REPOSITORY = "MDFillin/cup-of-tea"
}
