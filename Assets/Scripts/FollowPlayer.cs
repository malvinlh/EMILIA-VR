using UnityEngine;
using UnityEngine.AI;

public class FollowPlayer : MonoBehaviour
{
    public Transform companionAnchor;
    public Transform playerHead;
    public float followDistance = 0.6f;
    public float updateThreshold = 0.2f;

    NavMeshAgent agent;
    Animator anim;
    Vector3 lastTargetPos;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        anim = GetComponent<Animator>();
        lastTargetPos = companionAnchor.position;
    }

    void Update()
    {
        float dist = Vector3.Distance(transform.position, companionAnchor.position);

        if (dist > followDistance &&
            Vector3.Distance(lastTargetPos, companionAnchor.position) > updateThreshold)
        {
            agent.SetDestination(companionAnchor.position);
            lastTargetPos = companionAnchor.position;
        }

        anim.SetFloat("Speed", agent.velocity.magnitude);

        HandleRotation();
    }

    void HandleRotation()
    {
        if (agent.velocity.magnitude > 0.1f)
        {
            Vector3 dir = agent.velocity.normalized;
            dir.y = 0;
            Rotate(dir);
        }
        else
        {
            Vector3 lookDir = playerHead.position - transform.position;
            lookDir.y = 0;
            Rotate(lookDir);
        }
    }

    void Rotate(Vector3 dir)
    {
        if (dir == Vector3.zero) return;

        transform.rotation = Quaternion.Slerp(
            transform.rotation,
            Quaternion.LookRotation(dir),
            Time.deltaTime * 4f
        );
    }
}
