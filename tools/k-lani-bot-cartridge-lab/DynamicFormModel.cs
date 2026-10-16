namespace KLaniBot.CartridgeLab;

// The private lab varies the same native controls between local and delivery operations.
public enum DeliveryMode
{
    Local,
    Delivery
}

// The lab exposes a finite choice set whose meaning changes without replacing its widget.
public enum DeliveryOption
{
    Collect,
    Courier,
    Express
}

// The verifier reads this actual persisted state after the operator confirms the modal dialog.
public sealed record DynamicFormResult(DeliveryMode Mode, DeliveryOption Option, string Address, int Revision);

// The native fixture uses this state to reject stale choices and changes behind a modal dialog.
public sealed class DynamicFormModel
{
    public DeliveryMode Mode
    {
        get; private set;
    }
    public DeliveryOption Option
    {
        get; private set;
    }
    public string Address { get; private set; } = "";
    public int Revision
    {
        get; private set;
    }
    public bool ModalOpen
    {
        get; private set;
    }
    public bool AddressVisible => Mode == DeliveryMode.Delivery;
    public DeliveryOption[] Options => Mode switch
    {
        DeliveryMode.Local => [DeliveryOption.Collect],
        DeliveryMode.Delivery => [DeliveryOption.Courier, DeliveryOption.Express],
        _ => throw new System.Diagnostics.UnreachableException()
    };

    public bool TryChangeMode(DeliveryMode mode)
    {
        if (ModalOpen || !Enum.IsDefined(mode))
        {
            return false;
        }

        Mode = mode;
        Option = Options[0];
        Address = "";
        Revision++;

        return true;
    }

    public bool TrySelectOption(DeliveryOption option, int observedRevision)
    {
        if (ModalOpen || observedRevision != Revision || !Options.Contains(option))
        {
            return false;
        }

        Option = option;

        return true;
    }

    public bool TrySetAddress(string address)
    {
        if (ModalOpen || !AddressVisible || address == null)
        {
            return false;
        }

        Address = address;

        return true;
    }

    public bool TryOpenConfirmation()
    {
        if (ModalOpen || AddressVisible && string.IsNullOrWhiteSpace(Address))
        {
            return false;
        }

        ModalOpen = true;

        return true;
    }

    public bool TryConfirm(out DynamicFormResult? result)
    {
        result = null;
        if (!ModalOpen)
        {
            return false;
        }

        result = new DynamicFormResult(Mode, Option, Address, Revision);
        ModalOpen = false;

        return true;
    }

    public void CancelConfirmation()
    {
        ModalOpen = false;
    }
}
